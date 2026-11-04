using System;

namespace System_Monitor.Models
{
    /// <summary>
    /// Policy for validating the Authenticode signature of the PawnIO installer.
    /// </summary>
    public enum SignerValidationPolicy
    {
        /// <summary>Require a valid Authenticode signature from the expected signer.</summary>
        RequireSigned,

        /// <summary>Only verify the SHA-256 hash — no signature check.</summary>
        HashOnly,
    }

    /// <summary>
    /// Trusted descriptor for the PawnIO installer download and launch.
    ///
    /// Contains the approved URL, expected version, SHA-256 hash, and
    /// signer-validation policy. These values are pinned (hardcoded) and
    /// must not be inferred from runtime data.
    ///
    /// Per the acceptance check, the previously reported hash and signer
    /// values from the design document are NOT treated as independently
    /// reverified. Their provenance is documented in the field comments.
    /// </summary>
    public sealed record TrustedPawnIoInstaller
    {
        /// <summary>The exact version this descriptor is for.</summary>
        public string Version { get; init; } = string.Empty;

        /// <summary>
        /// The approved download URL on the official GitHub releases page.
        /// Pinned: https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe
        /// </summary>
        public string Url { get; init; } = string.Empty;

        /// <summary>
        /// Expected SHA-256 hash of the downloaded file.
        /// Pinned value: 1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032
        ///
        /// Provenance: recorded from GitHub release asset metadata + third-party
        /// verification (soylab-edu/soylab_hw_temperature) + Cleanmeter security docs.
        /// NOT independently reververified in this review — Phase 2 implementation
        /// must re-verify at download time.
        /// </summary>
        public string ExpectedSha256 { get; init; } = string.Empty;

        /// <summary>
        /// Expected Authenticode signer subject.
        /// Pinned value: E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU
        ///
        /// Provenance: recorded from web search results during design phase.
        /// NOT independently reververified in this review.
        /// </summary>
        public string ExpectedSignerSubject { get; init; } = string.Empty;

        /// <summary>The signer validation policy to apply.</summary>
        public SignerValidationPolicy SignerPolicy { get; init; }

        /// <summary>
        /// The filename to extract from the URL for the downloaded file.
        /// </summary>
        public string FileName => System.IO.Path.GetFileName(new Uri(Url).AbsolutePath);
    }
}
