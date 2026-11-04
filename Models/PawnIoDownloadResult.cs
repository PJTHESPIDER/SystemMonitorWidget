using System;

namespace System_Monitor.Models
{
    /// <summary>
    /// Outcome of the download + integrity/signature verification step.
    /// </summary>
    public enum PawnIoDownloadOutcome
    {
        /// <summary>Download and verification succeeded.</summary>
        Verified,

        /// <summary>Download succeeded but SHA-256 hash did not match.</summary>
        HashMismatch,

        /// <summary>Download succeeded but Authenticode signature is missing or signer mismatch.</summary>
        SignatureRejected,

        /// <summary>HTTP/client error during download.</summary>
        DownloadError,

        /// <summary>Operation was cancelled before completion.</summary>
        Cancelled,
    }

    /// <summary>
    /// Result of downloading and verifying the PawnIO installer.
    /// Carries the path to the verified file (only when Outcome is Verified),
    /// the reason for any failure, and the downloaded size (informative).
    /// </summary>
    public sealed record PawnIoDownloadResult
    {
        /// <summary>Outcome of the download + verification.</summary>
        public PawnIoDownloadOutcome Outcome { get; init; }

        /// <summary>
        /// Path to the downloaded and verified file. Only set when Outcome is Verified.
        /// The file is owned by this operation and must be cleaned up.
        /// </summary>
        public string? FilePath { get; init; }

        /// <summary>Reason for failure when Outcome is not Verified.</summary>
        public string? FailureReason { get; init; }

        /// <summary>Downloaded file size in bytes (informative, even on failure).</summary>
        public long? BytesDownloaded { get; init; }

        /// <summary>When the download attempt completed.</summary>
        public DateTimeOffset CompletedAt { get; init; }

        /// <summary>True when the operation was cancelled by the caller.</summary>
        public bool WasCancelled { get; init; }
    }
}
