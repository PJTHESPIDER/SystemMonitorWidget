using System;
using System.Threading;
using System.Threading.Tasks;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// Downloads a file from a trusted URL and verifies its integrity against
    /// the expected SHA-256 hash and Authenticode signature policy.
    ///
    /// This is the download transport abstraction — separated from the installer
    /// launch and post-install verification so each can be tested independently.
    /// Implementations must not execute or open the downloaded file.
    /// </summary>
    public interface ITrustedDownloader
    {
        /// <summary>
        /// Downloads the file at <paramref name="url"/> to a controlled directory,
        /// verifies its SHA-256 against <paramref name="expectedSha256"/>, and
        /// validates the Authenticode signature if <paramref name="signerPolicy"/>
        /// requires it.
        /// </summary>
        /// <param name="url">The URL to download from.</param>
        /// <param name="expectedSha256">The expected SHA-256 hash in hex.</param>
        /// <param name="signerPolicy">The signature validation policy.</param>
        /// <param name="expectedSignerSubject">The expected signer subject, if checking signatures.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The download result with verification outcome.</returns>
        Task<PawnIoDownloadResult> DownloadAndVerifyAsync(
            string url,
            string expectedSha256,
            SignerValidationPolicy signerPolicy,
            string? expectedSignerSubject,
            CancellationToken cancellationToken = default);
    }
}
