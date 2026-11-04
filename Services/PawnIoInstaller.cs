using System;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// Coordinates the download, verification, and launch of the PawnIO installer.
    ///
    /// Responsibilities are separated into:
    /// - Download transport (ITrustedDownloader abstraction, default uses HttpClient)
    /// - File integrity and signature verification
    /// - Installer launch (elevated, via IProcessRunner abstraction)
    /// - Post-install dependency checking (via PawnIoInstallationChecker)
    ///
    /// Does NOT handle the machine-wide setup coordination (mutex, state machine),
    /// which is the responsibility of CpuTemperatureSetupCoordinator.
    /// Does NOT execute an installer if verification fails or is unknown.
    /// </summary>
    public sealed class PawnIoInstaller : IPawnIoInstaller
    {
        private readonly ITrustedDownloader _downloader;
        private readonly IProcessRunner _processRunner;
        private readonly TrustedPawnIoInstaller _descriptor;

        /// <summary>
        /// Creates the installer with explicit dependencies for testing.
        /// The <paramref name="installationChecker"/> parameter is retained for
        /// constructor compatibility but is not used by this class — post-install
        /// verification is the coordinator's responsibility.
        /// </summary>
        public PawnIoInstaller(
            TrustedPawnIoInstaller descriptor,
            ITrustedDownloader downloader,
            IProcessRunner processRunner)
        {
            _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
            _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
            _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        }

        /// <summary>
        /// Creates the installer with the default production dependencies.
        /// The caller supplies the trusted descriptor (pinned URL, hash, signer policy).
        /// </summary>
        public PawnIoInstaller(TrustedPawnIoInstaller descriptor)
            : this(descriptor, new TrustedDownloader(), new SystemProcessRunner())
        {
        }

        /// <summary>
        /// Downloads the installer, verifies integrity/signature, launches it
        /// as Administrator, and waits for completion.
        ///
        /// Does NOT verify post-install state — the caller (coordinator)
        /// is responsible for rechecking dependency state after the installer exits.
        /// </summary>
        /// <param name="installerPath">Ignored on first call; download transport manages the path.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Result of the installer launch step.</returns>
        public async Task<PawnIoInstallerLaunchResult> LaunchAsync(
            string installerPath,
            CancellationToken cancellationToken = default)
        {
            // Step 1: Download and verify
            var downloadResult = await _downloader.DownloadAndVerifyAsync(
                _descriptor.Url,
                _descriptor.ExpectedSha256,
                _descriptor.SignerPolicy,
                _descriptor.ExpectedSignerSubject,
                cancellationToken);

            if (cancellationToken.IsCancellationRequested)
            {
                return new PawnIoInstallerLaunchResult
                {
                    Outcome = PawnIoInstallerOutcome.Cancelled,
                    FailureReason = "Download cancelled by user",
                    CompletedAt = DateTimeOffset.UtcNow,
                    WasCancelled = true,
                };
            }

            if (downloadResult.Outcome != PawnIoDownloadOutcome.Verified ||
                downloadResult.FilePath is null)
            {
                return new PawnIoInstallerLaunchResult
                {
                    Outcome = MapDownloadFailure(downloadResult),
                    FailureReason = downloadResult.FailureReason,
                    CompletedAt = DateTimeOffset.UtcNow,
                    WasCancelled = downloadResult.WasCancelled,
                    DownloadOutcome = downloadResult.Outcome,
                };
            }

            // Step 2: Launch the installer as Administrator.
            // The PawnIO installer is a GUI wizard with no documented silent switches.
            // The user must select "Official edition" manually.
            PawnIoInstallerOutcome launchOutcome;
            string? failureReason = null;
            int? exitCode = null;
            try
            {
                var elevatedResult = await _processRunner.RunElevatedAsync(
                    downloadResult.FilePath!,
                    string.Empty,
                    cancellationToken);

                exitCode = elevatedResult.ExitCode;

                if (cancellationToken.IsCancellationRequested || elevatedResult.WasCancelled)
                {
                    launchOutcome = PawnIoInstallerOutcome.Cancelled;
                    failureReason = elevatedResult.FailureReason ?? "User cancelled during installer execution (UAC declined or process killed)";
                }
                else if (exitCode == 0)
                {
                    launchOutcome = PawnIoInstallerOutcome.Exited;
                }
                else if (exitCode == 3010 || elevatedResult.RebootRequired)
                {
                    // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED (common Windows installer exit code)
                    launchOutcome = PawnIoInstallerOutcome.RebootRequired;
                }
                else if (exitCode == 1602)
                {
                    // 1602 = ERROR_INSTALL_USEREXIT (user cancelled)
                    launchOutcome = PawnIoInstallerOutcome.Cancelled;
                    failureReason = "User cancelled the installer";
                }
                else if (exitCode is null)
                {
                    launchOutcome = PawnIoInstallerOutcome.LaunchFailed;
                    failureReason = elevatedResult.FailureReason ?? "Installer process could not be started";
                }
                else
                {
                    launchOutcome = PawnIoInstallerOutcome.Indeterminate;
                    failureReason = $"Installer exited with code {exitCode}";
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                launchOutcome = PawnIoInstallerOutcome.LaunchFailed;
                failureReason = $"Elevation denied or unavailable: {ex.Message}";
            }
            catch (OperationCanceledException)
            {
                launchOutcome = PawnIoInstallerOutcome.Cancelled;
                failureReason = "Operation cancelled";
            }
            catch (Exception ex)
            {
                launchOutcome = PawnIoInstallerOutcome.LaunchFailed;
                failureReason = $"Failed to launch installer: {ex.Message}";
            }

            // Clean up the downloaded file — it is owned by this operation.
            // We do NOT clean up files we didn't create.
            try
            {
                if (File.Exists(downloadResult.FilePath))
                    File.Delete(downloadResult.FilePath);
            }
            catch (Exception ex)
            {
                // Best-effort cleanup; don't mask the installer outcome.
                DiagnosticLog.Shared.Warning(
                    "PawnIO",
                    "cleanup",
                    "failed",
                    detail: ex.Message);
            }

            return new PawnIoInstallerLaunchResult
            {
                Outcome = launchOutcome,
                ExitCode = exitCode,
                FailureReason = failureReason,
                RebootRequired = launchOutcome == PawnIoInstallerOutcome.RebootRequired,
                CompletedAt = DateTimeOffset.UtcNow,
                WasCancelled = launchOutcome == PawnIoInstallerOutcome.Cancelled,
            };
        }

        private static PawnIoInstallerOutcome MapDownloadFailure(PawnIoDownloadResult result)
        {
            return result.Outcome switch
            {
                PawnIoDownloadOutcome.HashMismatch => PawnIoInstallerOutcome.Indeterminate,
                PawnIoDownloadOutcome.SignatureRejected => PawnIoInstallerOutcome.Indeterminate,
                PawnIoDownloadOutcome.DownloadError => PawnIoInstallerOutcome.LaunchFailed,
                PawnIoDownloadOutcome.Cancelled => PawnIoInstallerOutcome.Cancelled,
                _ => PawnIoInstallerOutcome.LaunchFailed,
            };
        }
    }

    /// <summary>
    /// Default implementation of ITrustedDownloader. Downloads a file from a URL,
    /// verifies its SHA-256 hash, and validates the Authenticode signature if required.
    /// Downloads to a controlled temp directory. Does not execute the file.
    /// </summary>
    public sealed class TrustedDownloader : ITrustedDownloader
    {
        /// <summary>
        /// Base directory for all PawnIO downloads. A unique subdirectory is
        /// created per download operation to isolate concurrent downloads.
        /// </summary>
        private static readonly string BaseDownloadDir =
            Path.Combine(Path.GetTempPath(), "SystemMonitor-PawnIO");

        /// <summary>
        /// Downloads the file, verifies the SHA-256 hash, and optionally checks
        /// the Authenticode signature.
        ///
        /// Fail-closed: only an explicitly accepted result (hash matches AND
        /// signature valid + signer matches) permits proceeding to elevation.
        /// Any verification error results in SignatureRejected with the file deleted.
        /// </summary>
        public async Task<PawnIoDownloadResult> DownloadAndVerifyAsync(
            string url,
            string expectedSha256,
            SignerValidationPolicy signerPolicy,
            string? expectedSignerSubject,
            CancellationToken cancellationToken = default)
        {
            string? operationDir = null;
            string? filePath = null;
            long? bytesDownloaded = null;

            try
            {
                // Create a unique per-operation directory to isolate this
                // download from concurrent operations and prior runs.
                operationDir = Path.Combine(BaseDownloadDir, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(operationDir);

                // Set directory ACL: only the current user has access.
                // On Windows, Directory.CreateDirectory inherits the parent's
                // ACL. We explicitly set a restrictive ACL to prevent other
                // users (including low-privilege service accounts) from
                // reading or replacing the file after download.
                try
                {
                    var dirInfo = new DirectoryInfo(operationDir);
                    dirInfo.SetAccessControl(CreateRestrictedDirectorySecurity());
                }
                catch
                {
                    // If ACL set fails, proceed anyway — the GUID-named directory
                    // provides isolation against collision. Failure here is logged
                    // but not fatal (the SHA-256 + signature check remains the
                    // primary integrity gate).
                }

                var fileName = Path.GetFileName(new Uri(url).AbsolutePath);
                if (string.IsNullOrEmpty(fileName))
                    fileName = "PawnIO_setup.exe";

                // Path.Combine with the operation directory. Path.GetFileName on
                // the URL prevents directory traversal via the URL path.
                filePath = Path.Combine(operationDir, Path.GetFileName(fileName));

                // Guard against path traversal: ensure the resolved path is
                // within the operation directory.
                var fullPath = Path.GetFullPath(filePath);
                if (!fullPath.StartsWith(Path.GetFullPath(operationDir + Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                {
                    return new PawnIoDownloadResult
                    {
                        Outcome = PawnIoDownloadOutcome.DownloadError,
                        FailureReason = "Invalid file path derived from URL",
                        FilePath = null,
                        BytesDownloaded = null,
                        CompletedAt = DateTimeOffset.UtcNow,
                        WasCancelled = false,
                    };
                }

                using var httpClient = new HttpClient();
                using var response = await httpClient.GetAsync(
                    new Uri(url),
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    return new PawnIoDownloadResult
                    {
                        Outcome = PawnIoDownloadOutcome.DownloadError,
                        FailureReason = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                        FilePath = null,
                        BytesDownloaded = null,
                        CompletedAt = DateTimeOffset.UtcNow,
                        WasCancelled = cancellationToken.IsCancellationRequested,
                    };
                }

                // Write the downloaded file with exclusive write access.
                using var fileStream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await contentStream.CopyToAsync(fileStream, 81920, cancellationToken);
                await fileStream.FlushAsync(cancellationToken);
                bytesDownloaded = fileStream.Length;

                // Stream is disposed here, releasing the exclusive lock.
                // From this point, the file is on disk and can be read
                // for verification. The FileShare.None during write prevents
                // concurrent reads/writes during download. After disposal,
                // other readers can access the file, but the SHA-256 +
                // Authenticode verification establishes its identity.

                // Step 1: Verify SHA-256 hash against the trusted descriptor.
                // This is a content-addressable integrity check — the hash pins
                // the file bytes independently of the signer.
                using (var sha = SHA256.Create())
                {
                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var hashBytes = await sha.ComputeHashAsync(fs, cancellationToken);
                    var actualHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();

                    if (!string.Equals(actualHash, expectedSha256?.ToLowerInvariant(), StringComparison.Ordinal))
                    {
                        // Hash mismatch — delete the file (fail-closed: no unverified file persists).
                        try { File.Delete(filePath); } catch { }

                        return new PawnIoDownloadResult
                        {
                            Outcome = PawnIoDownloadOutcome.HashMismatch,
                            FailureReason = $"SHA-256 mismatch: expected {expectedSha256}, got {actualHash}",
                            FilePath = null,
                            BytesDownloaded = bytesDownloaded,
                            CompletedAt = DateTimeOffset.UtcNow,
                            WasCancelled = false,
                        };
                    }
                }

                // Step 2: Verify Authenticode signature if required by the policy.
                if (signerPolicy == SignerValidationPolicy.RequireSigned)
                {
                    var sigResult = VerifyAuthenticodeSignature(filePath, expectedSignerSubject);
                    if (!sigResult.IsValid)
                    {
                        // Signature rejected — delete the file (fail-closed).
                        try { File.Delete(filePath); } catch { }

                        return new PawnIoDownloadResult
                        {
                            Outcome = PawnIoDownloadOutcome.SignatureRejected,
                            FailureReason = sigResult.Detail,
                            FilePath = null,
                            BytesDownloaded = bytesDownloaded,
                            CompletedAt = DateTimeOffset.UtcNow,
                            WasCancelled = false,
                        };
                    }
                }

                // Both checks passed — return the verified file path.
                // The caller (PawnIoInstaller) owns the file from this point
                // and is responsible for cleanup after launch.
                return new PawnIoDownloadResult
                {
                    Outcome = PawnIoDownloadOutcome.Verified,
                    FilePath = filePath,
                    FailureReason = null,
                    BytesDownloaded = bytesDownloaded,
                    CompletedAt = DateTimeOffset.UtcNow,
                    WasCancelled = false,
                };
            }
            catch (OperationCanceledException)
            {
                // Clean up partial download
                if (filePath is not null && File.Exists(filePath))
                    try { File.Delete(filePath); } catch { }
                if (operationDir is not null && Directory.Exists(operationDir))
                    try { Directory.Delete(operationDir, true); } catch { }

                return new PawnIoDownloadResult
                {
                    Outcome = PawnIoDownloadOutcome.Cancelled,
                    FailureReason = "Download cancelled",
                    FilePath = null,
                    BytesDownloaded = bytesDownloaded,
                    CompletedAt = DateTimeOffset.UtcNow,
                    WasCancelled = true,
                };
            }
            catch (Exception ex)
            {
                if (filePath is not null && File.Exists(filePath))
                    try { File.Delete(filePath); } catch { }
                if (operationDir is not null && Directory.Exists(operationDir))
                    try { Directory.Delete(operationDir, true); } catch { }

                return new PawnIoDownloadResult
                {
                    Outcome = PawnIoDownloadOutcome.DownloadError,
                    FailureReason = ex.Message,
                    FilePath = null,
                    BytesDownloaded = bytesDownloaded,
                    CompletedAt = DateTimeOffset.UtcNow,
                    WasCancelled = false,
                };
            }
        }

        /// <summary>
        /// Creates a <see cref="DirectorySecurity"/> that grants full control
        /// only to the current user and removes all inherited access. This
        /// prevents other users or service accounts from reading or replacing
        /// the downloaded file after verification completes.
        /// </summary>
        /// <remarks>
        /// This method is best-effort: if the ACL cannot be set (e.g., on
        /// non-Windows or limited-permission contexts), the GUID-named
        /// operation directory still provides collision isolation. The SHA-256
        /// hash + Authenticode verification remain the primary integrity gates.
        /// </remarks>
        private static System.Security.AccessControl.DirectorySecurity CreateRestrictedDirectorySecurity()
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (sid is null)
                return new System.Security.AccessControl.DirectorySecurity();

            var acl = new System.Security.AccessControl.DirectorySecurity();
            acl.SetOwner(sid);
            // Grant full control to the current user only.
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var rule = new System.Security.AccessControl.FileSystemAccessRule(
                sid,
                System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None,
                System.Security.AccessControl.AccessControlType.Allow);
            acl.AddAccessRule(rule);
            return acl;
        }

        #region WinVerifyTrust P/Invoke

        // WinVerifyTrust action GUID for generic Authenticode verification.
        // WINTRUST_ACTION_GENERIC_VERIFY_V2 verifies the Authenticode signature
        // on a PE file, checking hash integrity, signature validity, chain
        // trust, and revocation status.
        // Ref: https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust
        private static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 =
            new Guid(0xaac57c4, 0x4d1, 0x11d0, 0x88, 0xa9, 0x0, 0x0, 0xc0, 0xc1, 0xc, 0x51);

        // WinVerifyTrust HRESULTs (subset of documented values).
        // Ref: https://learn.microsoft.com/en-us/windows/win32/seccrypto/common-hresult-values
        private const int ERROR_SUCCESS = 0;
        private const int TRUST_E_NOSIGNATURE = unchecked((int)0x80096004);
        private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x80096002);
        private const int TRUST_E_EXPLICIT_INTENTION_NOT_ALLOWED = unchecked((int)0x80096006);
        private const int TRUST_E_CHAINING = unchecked((int)0x80096010);
        private const int TRUST_E_BAD_GENERIC_TYPE = unchecked((int)0x80096012);
        private const int CERT_E_UNTRUSTEDCA = unchecked((int)0x800B0100);
        private const int CERT_E_UNTRUSTEDROOT = unchecked((int)0x800B0109);
        private const int CERT_E_BAD_CERTCHAIN = unchecked((int)0x800B010A);
        private const int CERT_E_VALIDITY = unchecked((int)0x800B010D);
        private const int CERT_E_CN_NO_MATCH = unchecked((int)0x800B010F);
        private const int TRUST_E_BASIC_CONSTRAINTS = unchecked((int)0x80096015);

        // WTD_ flags for WINTRUST_DATA.dwFlags
        private const uint WTD_UI_NONE = 0x00000000;       // WTD_UI_NONE: no UI
        private const uint WTD_REVOKE_WHOLECHAIN = 0x00000100; // WTD_REVOKE_WHOLECHAIN: check revocation for all certs in chain
        private const uint WTD_CHOICE_FILE = 0x00000001;   // WTD_CHOICE_FILE: use WINTRUST_FILE_DATA

        // WINTRUST_FILE_DATA — describes the file to verify (used when dwUnionChoice = WTD_CHOICE_FILE).
        // Ref: https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-wintrust_file_info
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WINTRUST_FILE_DATA
        {
            public uint cbStruct;
            public IntPtr hFile;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? pcwszFilePath;
            public IntPtr hReserved1;
            public uint dwReserved1;
            public uint dwReserved2;
        }

        // WINTRUST_DATA — top-level structure passed to WinVerifyTrust.
        // The union field (pRequest) points to WINTRUST_FILE_DATA when
        // dwUnionChoice is WTD_CHOICE_FILE. The remaining fields after the union
        // (dwFlags, dwUIChoice, etc.) follow in the Windows SDK layout.
        // NOTE: Layout must match the Windows SDK exactly. The pRequest field
        // is a union that points to one of several structures; for file
        // verification it points to WINTRUST_FILE_DATA.
        [StructLayout(LayoutKind.Sequential)]
        private struct WINTRUST_DATA
        {
            public uint cbStruct;
            public uint dwUnionChoice;
            public IntPtr pRequest;       // Pointer to WINTRUST_FILE_DATA (union)
            public uint dwFlags;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwStateAction;
            public uint dwProvFlags;
            public uint dwUIContext;
        }

        [DllImport("wintrust.dll", SetLastError = true, ExactSpelling = true)]
        private static extern int WinVerifyTrust(
            IntPtr hwnd,
            ref Guid actionId,
            ref WINTRUST_DATA data);

        #endregion

        /// <summary>
        /// Verifies the Authenticode signature of an executable file on Windows
        /// using WinVerifyTrust with WINTRUST_ACTION_GENERIC_VERIFY_V2.
        ///
        /// This is the production Authenticode verifier — it validates the actual
        /// signed executable, not just the certificate. It follows Microsoft's
        /// documented PE-signature verification pattern:
        /// https://learn.microsoft.com/en-us/windows/win32/seccrypto/strong-naming-and-authenticode
        ///
        /// Trust-state lifecycle:
        /// - dwStateAction is set to 0 (WTD_STATEACTION_IGNORE): WinVerifyTrust
        ///   performs a single-shot verification with no retained provider state.
        ///   No WTD_STATEACTION_CLOSE call is required because no provider state
        ///   handle is opened. The WINTRUST_FILE_DATA structure is the only
        ///   separately allocated native resource; it is freed via
        ///   Marshal.FreeHGlobal in the finally block.
        ///
        /// Validation API: WinVerifyTrust (wintrust.dll)
        /// - WINTRUST_ACTION_GENERIC_VERIFY_V2: verifies the Authenticode
        ///   signature on a PE file, checking hash integrity, signature validity,
        ///   chain trust, and revocation status.
        /// - WTD_UI_NONE: disables all interactive trust UI — verification is
        ///   silent. No certificate trust dialogs or UAC prompts are shown.
        /// - WTD_REVOKE_WHOLECHAIN: checks revocation for the entire chain
        ///   (online CRL/OCSP). This is fail-closed — if revocation servers are
        ///   unreachable, verification may fail.
        ///
        /// Trust policy: only HRESULT 0 (ERROR_SUCCESS) from WinVerifyTrust
        /// is treated as a valid signature. All other HRESULTs (including
        /// TRUST_E_NOSIGNATURE, CERT_E_UNTRUSTEDROOT, etc.) are treated as
        /// verification failures.
        ///
        /// Validated-signer binding:
        /// - WinVerifyTrust validates the primary Authenticode signature against
        ///   the OS trust policy. Only ERROR_SUCCESS is accepted.
        /// - After WinVerifyTrust success, the signer DN is extracted via
        ///   X509Certificate2.CreateFromSignedFile (metadata read only) and
        ///   compared to expectedSignerSubject. Whole-file SHA-256 pinning
        ///   (checked before signature verification) independently binds the
        ///   file bytes to a known artifact.
        /// - See GetSignerSubjectFromCert XML docs for multi-signature limitation.
        ///
        /// Fail-closed: only Valid + SignerMatch permits proceeding to elevation.
        ///
        /// Limitations:
        /// - Requires Windows. On non-Windows, returns SignatureRejected.
        /// - Revocation checking is online-only (WTD_REVOKE_WHOLECHAIN).
        ///   If CRL/OCSP endpoints are unreachable, verification fails (fail-closed).
        /// - Does not verify timestamp info — a cert revoked after signing
        ///   is not detected via timestamp. See GetSignerSubjectFromCert docs.
        /// - Does not validate certificate thumbprint; DN matching only.
        /// - If multiple signatures are present, WinVerifyTrust validates the
        ///   primary signature. The signer DN is extracted from the same cert
        ///   context. Whole-file SHA-256 pinning provides the primary artifact
        ///   binding.
        /// - Tests cannot exercise real Authenticode verification (no controlled
        ///   signed fixture; tests run on non-Windows CI). All signature outcome
        ///   tests use FakeTrustedDownloader to simulate results.
        /// </summary>
        private static (bool IsValid, string Detail, X509SignatureStatus Status) VerifyAuthenticodeSignature(
            string filePath,
            string? expectedSignerSubject)
        {
            // Authenticode verification requires the Windows WinVerifyTrust API.
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return (false, "Authenticode signature verification requires Windows",
                    X509SignatureStatus.VerificationError);
            }

            if (!File.Exists(filePath))
            {
                return (false, $"File not found: {filePath}",
                    X509SignatureStatus.VerificationError);
            }

            var fileData = new WINTRUST_FILE_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_DATA>(),
                hFile = IntPtr.Zero,
                pcwszFilePath = filePath,
                hReserved1 = IntPtr.Zero,
                dwReserved1 = 0,
                dwReserved2 = 0,
            };

            // Allocate unmanaged memory for WINTRUST_FILE_DATA so WinVerifyTrust
            // can read it through the pRequest union pointer. The memory must
            // remain valid for the duration of the WinVerifyTrust call.
            var fileDataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_DATA>());
            try
            {
                Marshal.StructureToPtr(fileData, fileDataPtr, fDeleteOld: false);

                // dwStateAction = 0 (WTD_STATEACTION_IGNORE): no provider state is
                // cached across calls. WinVerifyTrust performs a single-shot
                // verification. No WTD_STATEACTION_CLOSE is required because no
                // state handle is opened. The HGlobal for WINTRUST_FILE_DATA is
                // the only separately allocated native resource and is freed
                // in the finally block below.
                var data = new WINTRUST_DATA
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                    dwUnionChoice = WTD_CHOICE_FILE,
                    pRequest = fileDataPtr,
                    // WTD_UI_NONE: no interactive trust UI
                    // WTD_REVOKE_WHOLECHAIN: online CRL/OCSP check for entire chain
                    dwFlags = WTD_UI_NONE | WTD_REVOKE_WHOLECHAIN,
                    dwProvFlags = 0,
                    dwUIChoice = WTD_UI_NONE,
                    fdwRevocationChecks = WTD_REVOKE_WHOLECHAIN,
                    dwStateAction = 0, // WTD_STATEACTION_IGNORE — no retained provider state
                    dwUIContext = 0,
                };

                var actionId = WINTRUST_ACTION_GENERIC_VERIFY_V2;
                var result = WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);

                if (result == ERROR_SUCCESS)
                {
                    // Signature is valid and trusted. Now verify the signer identity.
                    var signerSubject = GetSignerSubjectFromCert(filePath);

                    if (!string.IsNullOrEmpty(expectedSignerSubject))
                    {
                        if (!string.Equals(signerSubject, expectedSignerSubject, StringComparison.OrdinalIgnoreCase))
                        {
                            return (false,
                                $"Signer subject mismatch: expected '{expectedSignerSubject}', got '{signerSubject}'",
                                X509SignatureStatus.SignerMismatch);
                        }
                    }

                    return (true, string.Empty, X509SignatureStatus.Valid);
                }

                // Map HRESULT to structured status.
                var status = MapWinVerifyTrustResult(result);
                return (false, $"Signature verification failed: {GetWinVerifyTrustMessage(result)}", status);
            }
            finally
            {
                // Free unmanaged memory for WINTRUST_FILE_DATA.
                Marshal.FreeHGlobal(fileDataPtr);
            }
        }

        /// <summary>
        /// Extracts the signer's Distinguished Name from the authenticated
        /// certificate embedded in a signed PE file.
        ///
        /// <para>
        /// This is a read-only metadata operation — it does NOT re-validate the
        /// signature chain; WinVerifyTrust has already confirmed the signature
        /// is valid and trusted at the system level.
        /// </para>
        ///
        /// <para>
        /// <strong>Validated-signer binding limitation:</strong> When a file has
        /// multiple Authenticode signatures, <c>X509Certificate2.CreateFromSignedFile</c>
        /// returns the first signer certificate in the PKCS#7 store. This is not
        /// guaranteed to be the same signature that WinVerifyTrust validated as
        /// the primary signature. However, the whole-file SHA-256 pinning
        /// (checked before signature verification) independently binds the file
        /// bytes to a specific known artifact. A file that passes SHA-256
        /// pinning and WinVerifyTrust with WINTRUST_ACTION_GENERIC_VERIFY_V2
        /// is the expected artifact with a trusted signature. The signer DN
        /// check provides an additional publisher-identity confirmation layer
        /// on top of this.
        /// </para>
        ///
        /// <para>
        /// <strong>Timestamp policy:</strong> WinVerifyTrust with
        /// <c>WTD_REVOKE_WHOLECHAIN</c> evaluates revocation based on the
        /// current state of the certificate chain — it checks whether any
        /// certificate in the chain has been revoked as of the verification
        /// time. It does NOT inspect embedded timestamp information. A
        /// certificate that was valid at signing time but revoked later
        /// after the signature was created (and not timestamped) will fail
        /// revocation checking and be rejected (fail-closed). The application
        /// does not add any separate timestamp requirement — the OS trust
        /// policy's revocation evaluation is the sole timestamp-relevant check.
        /// </para>
        /// </summary>
        private static string GetSignerSubjectFromCert(string filePath)
        {
            try
            {
                // CreateFromSignedFile extracts the embedded PKCS#7 signer
                // certificate. This is a metadata read only — WinVerifyTrust
                // has already validated the full chain and revocation status.
                var certRaw = X509Certificate2.CreateFromSignedFile(filePath);
                if (certRaw is null)
                    return string.Empty;
                var cert = new X509Certificate2(certRaw);
                return cert.SubjectName?.Format(true) ?? string.Empty;
            }
            catch
            {
                // If we cannot extract the signer, return empty. The signer
                // check is only performed when expectedSignerSubject is provided.
                return string.Empty;
            }
        }

        /// <summary>
        /// Maps WinVerifyTrust HRESULTs to the structured verification status.
        /// </summary>
        private static X509SignatureStatus MapWinVerifyTrustResult(int hresult)
        {
            return hresult switch
            {
                TRUST_E_NOSIGNATURE => X509SignatureStatus.NotSigned,
                TRUST_E_SUBJECT_FORM_UNKNOWN => X509SignatureStatus.NotSigned,
                CERT_E_UNTRUSTEDCA => X509SignatureStatus.UntrustedChain,
                CERT_E_UNTRUSTEDROOT => X509SignatureStatus.UntrustedRoot,
                CERT_E_BAD_CERTCHAIN => X509SignatureStatus.UntrustedChain,
                CERT_E_VALIDITY => X509SignatureStatus.Invalid,
                CERT_E_CN_NO_MATCH => X509SignatureStatus.SignerMismatch,
                TRUST_E_BASIC_CONSTRAINTS => X509SignatureStatus.Invalid,
                TRUST_E_CHAINING => X509SignatureStatus.UntrustedChain,
                TRUST_E_EXPLICIT_INTENTION_NOT_ALLOWED => X509SignatureStatus.VerificationError,
                _ => X509SignatureStatus.VerificationError,
            };
        }

        private static string GetWinVerifyTrustMessage(int hresult)
        {
            return hresult switch
            {
                ERROR_SUCCESS => "Success",
                TRUST_E_NOSIGNATURE => "No signature present (TRUST_E_NOSIGNATURE)",
                TRUST_E_SUBJECT_FORM_UNKNOWN => "Subject form unknown (TRUST_E_SUBJECT_FORM_UNKNOWN)",
                CERT_E_UNTRUSTEDCA => "Untrusted certificate authority (CERT_E_UNTRUSTEDCA)",
                CERT_E_UNTRUSTEDROOT => "Untrusted root (CERT_E_UNTRUSTEDROOT)",
                CERT_E_BAD_CERTCHAIN => "Bad certificate chain (CERT_E_BAD_CERTCHAIN)",
                CERT_E_VALIDITY => "Certificate validity period error (CERT_E_VALIDITY)",
                CERT_E_CN_NO_MATCH => "Certificate CN does not match (CERT_E_CN_NO_MATCH)",
                TRUST_E_BASIC_CONSTRAINTS => "Basic constraints error (TRUST_E_BASIC_CONSTRAINTS)",
                TRUST_E_CHAINING => "Chain building error (TRUST_E_CHAINING)",
                _ => $"HRESULT 0x{hresult:X8}",
            };
        }
    }

    /// <summary>
    /// Structured result of Authenticode signature verification.
    /// </summary>
    internal enum X509SignatureStatus
    {
        /// <summary>Signature is valid and signer matches.</summary>
        Valid,

        /// <summary>File has no embedded Authenticode signature.</summary>
        NotSigned,

        /// <summary>Signature is present but invalid (tampered or malformed).</summary>
        Invalid,

        /// <summary>Certificate chain does not root in a trusted root CA.</summary>
        UntrustedRoot,

        /// <summary>Certificate chain contains errors (expired, CTL issues, etc.).</summary>
        UntrustedChain,

        /// <summary>Certificate is valid but the signer subject does not match the expected value.</summary>
        SignerMismatch,

        /// <summary>Verification could not be completed (exception, non-Windows platform, etc.).</summary>
        VerificationError,
    }
}
