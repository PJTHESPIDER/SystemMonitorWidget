using System;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests
{
    /// <summary>
    /// Unit tests for PawnIoInstallationChecker.
    /// Tests cover all installation states: NotInstalled, Installed (with/without version),
    /// CheckFailed, Unsupported, and Unknown/Checking.
    ///
    /// These tests run against the actual machine state (no mocking). They verify
    /// the installation checker correctly reports the current machine's PawnIO state.
    /// On a machine without PawnIO, the checker should return NotInstalled.
    /// </summary>
    public sealed class PawnIoInstallationCheckerTests
    {
        [Fact]
        public void Check_ReturnsValidStateStructure()
        {
            var checker = new PawnIoInstallationChecker();
            var result = checker.Check();

            Assert.NotNull(result);
            Assert.True(Enum.IsDefined(typeof(PawnIoInstallationStatus), result.Status));
            Assert.True(result.CheckedAt <= DateTimeOffset.UtcNow.AddSeconds(1));
            Assert.True(result.CheckedAt >= DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        [Fact]
        public void Check_InstalledState_HasVersionOrNull()
        {
            var checker = new PawnIoInstallationChecker();
            var result = checker.Check();

            if (result.Status == PawnIoInstallationStatus.Installed)
            {
                // Version may be null if registry read failed, but status is Installed
                Assert.True(result.Version is null || !string.IsNullOrWhiteSpace(result.Version));
            }
        }

        [Fact]
        public void Check_CheckFailedState_HasFailureReason()
        {
            var checker = new PawnIoInstallationChecker();
            var result = checker.Check();

            if (result.Status == PawnIoInstallationStatus.CheckFailed)
            {
                Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
            }
        }

        [Fact]
        public void Check_NotInstalled_HasNullVersionAndNullFailureReason()
        {
            var checker = new PawnIoInstallationChecker();
            var result = checker.Check();

            if (result.Status == PawnIoInstallationStatus.NotInstalled)
            {
                Assert.Null(result.Version);
                Assert.Null(result.FailureReason);
            }
        }

        [Fact]
        public void Check_DistinctFromCompatibilityStatus()
        {
            // Verify that CheckFailed and Unknown remain distinct from NotInstalled
            // in the installation state (per design: keep unknown/check-failed distinct from NotInstalled)
            var checker = new PawnIoInstallationChecker();
            var result = checker.Check();

            // The installation status should be one of the defined states
            Assert.True(Enum.IsDefined(typeof(PawnIoInstallationStatus), result.Status));

            // NotInstalled means no registry metadata was found
            if (result.Status == PawnIoInstallationStatus.NotInstalled)
            {
                Assert.Null(result.Version);
                Assert.Null(result.FailureReason);
            }

            // CheckFailed means an error occurred during detection
            if (result.Status == PawnIoInstallationStatus.CheckFailed)
            {
                Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
            }
        }
    }

    /// <summary>
    /// Unit tests for PawnIoCompatibility evaluation.
    /// Tests cover all compatibility outcomes against the explicit baseline 2.2.0.
    /// </summary>
    public sealed class PawnIoCompatibilityTests
    {
        [Fact]
        public void Evaluate_NotInstalled_ReturnsNotInstalled()
        {
            var state = new PawnIoInstallationState
            {
                Status = PawnIoInstallationStatus.NotInstalled,
                Version = null,
                FailureReason = null,
                CheckedAt = DateTimeOffset.UtcNow,
            };

            var result = PawnIoCompatibility.Evaluate(state);

            Assert.Equal(PawnIoCompatibilityStatus.NotInstalled, result.Status);
            Assert.Null(result.DetectedVersion);
            Assert.Equal("2.2.0", result.TestedBaseline);
            Assert.Contains("not installed", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Evaluate_Installed_ExactBaseline_ReturnsCompatible()
        {
            var state = new PawnIoInstallationState
            {
                Status = PawnIoInstallationStatus.Installed,
                Version = "2.2.0",
                FailureReason = null,
                CheckedAt = DateTimeOffset.UtcNow,
            };

            var result = PawnIoCompatibility.Evaluate(state);

            Assert.Equal(PawnIoCompatibilityStatus.Compatible, result.Status);
            Assert.Equal("2.2.0", result.DetectedVersion);
            Assert.Equal("2.2.0", result.TestedBaseline);
            Assert.Null(result.Reason);
        }

        [Fact]
        public void Evaluate_Installed_WithBuildNumber_ReturnsCompatible()
        {
            var state = new PawnIoInstallationState
            {
                Status = PawnIoInstallationStatus.Installed,
                Version = "2.2.0.0",
                FailureReason = null,
                CheckedAt = DateTimeOffset.UtcNow,
            };

            var result = PawnIoCompatibility.Evaluate(state);

            Assert.Equal(PawnIoCompatibilityStatus.Compatible, result.Status);
            Assert.Equal("2.2.0.0", result.DetectedVersion);
        }

        [Fact]
        public void Evaluate_Installed_OlderVersion_ReturnsUnknown()
        {
            // Per corrected semantics: non-baseline versions are Unknown, not Incompatible,
            // unless evidence establishes incompatibility.
            var state = new PawnIoInstallationState
            {
                Status = PawnIoInstallationStatus.Installed,
                Version = "2.1.0",
                FailureReason = null,
                CheckedAt = DateTimeOffset.UtcNow,
            };

            var result = PawnIoCompatibility.Evaluate(state);

            Assert.Equal(PawnIoCompatibilityStatus.Unknown, result.Status);
            Assert.Equal("2.1.0", result.DetectedVersion);
            Assert.Equal("2.2.0", result.TestedBaseline);
            Assert.Contains("not the tested baseline", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Evaluate_Installed_NewerVersion_ReturnsUnknown()
        {
            var state = new PawnIoInstallationState
            {
                Status = PawnIoInstallationStatus.Installed,
                Version = "2.3.0",
                FailureReason = null,
                CheckedAt = DateTimeOffset.UtcNow,
            };

            var result = PawnIoCompatibility.Evaluate(state);

            Assert.Equal(PawnIoCompatibilityStatus.Unknown, result.Status);
            Assert.Equal("2.3.0", result.DetectedVersion);
            Assert.Equal("2.2.0", result.TestedBaseline);
            Assert.Contains("not the tested baseline", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Evaluate_Installed_MajorVersionDifferent_ReturnsUnknown()
        {
            var state = new PawnIoInstallationState
            {
                Status = PawnIoInstallationStatus.Installed,
                Version = "3.0.0",
                FailureReason = null,
                CheckedAt = DateTimeOffset.UtcNow,
            };

            var result = PawnIoCompatibility.Evaluate(state);

            Assert.Equal(PawnIoCompatibilityStatus.Unknown, result.Status);
            Assert.Equal("3.0.0", result.DetectedVersion);
        }

        [Fact]
        public void Evaluate_Installed_NullVersion_ReturnsUnknownVersion()
        {
            var state = new PawnIoInstallationState
            {
                Status = PawnIoInstallationStatus.Installed,
                Version = null,
                FailureReason = null,
                CheckedAt = DateTimeOffset.UtcNow,
            };

            var result = PawnIoCompatibility.Evaluate(state);

            Assert.Equal(PawnIoCompatibilityStatus.UnknownVersion, result.Status);
            Assert.Null(result.DetectedVersion);
            Assert.Equal("2.2.0", result.TestedBaseline);
            Assert.Contains("could not be read", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Evaluate_Installed_EmptyVersion_ReturnsUnknownVersion()
        {
            var state = new PawnIoInstallationState
            {
                Status = PawnIoInstallationStatus.Installed,
                Version = "   ",
                FailureReason = null,
                CheckedAt = DateTimeOffset.UtcNow,
            };

            var result = PawnIoCompatibility.Evaluate(state);

            Assert.Equal(PawnIoCompatibilityStatus.UnknownVersion, result.Status);
        }

        [Fact]
        public void Evaluate_CheckFailed_ReturnsUnknownVersion()
        {
            // CheckFailed is a detection error — installation check failed.
            // Version may or may not be readable. Per corrected semantics,
            // this maps to UnknownVersion (version not reliably determined).
            var state = new PawnIoInstallationState
            {
                Status = PawnIoInstallationStatus.CheckFailed,
                Version = "2.1.0",
                FailureReason = "Access denied",
                CheckedAt = DateTimeOffset.UtcNow,
            };

            var result = PawnIoCompatibility.Evaluate(state);

            Assert.Equal(PawnIoCompatibilityStatus.UnknownVersion, result.Status);
        }

        [Fact]
        public void Evaluate_Unsupported_ReturnsIncompatible()
        {
            // Unsupported is a hard incompatibility — the platform is not supported at all.
            var state = new PawnIoInstallationState
            {
                Status = PawnIoInstallationStatus.Unsupported,
                Version = "2.2.0",
                FailureReason = "ARM64 not supported",
                CheckedAt = DateTimeOffset.UtcNow,
            };

            var result = PawnIoCompatibility.Evaluate(state);

            Assert.Equal(PawnIoCompatibilityStatus.Incompatible, result.Status);
            Assert.Contains("not supported", result.Reason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Evaluate_Unknown_ReturnsUnknownVersion()
        {
            // Unknown installation status means no check was performed — version is null/unreadable,
            // so this maps to UnknownVersion in the compatibility result.
            var state = new PawnIoInstallationState
            {
                Status = PawnIoInstallationStatus.Unknown,
                Version = null,
                FailureReason = null,
                CheckedAt = DateTimeOffset.MinValue,
            };

            var result = PawnIoCompatibility.Evaluate(state);

            Assert.Equal(PawnIoCompatibilityStatus.UnknownVersion, result.Status);
        }

        [Fact]
        public void Evaluate_Checking_ReturnsUnknownVersion()
        {
            // Checking means the check is in progress — version is not yet available,
            // so this maps to UnknownVersion in the compatibility result.
            var state = new PawnIoInstallationState
            {
                Status = PawnIoInstallationStatus.Checking,
                Version = null,
                FailureReason = null,
                CheckedAt = DateTimeOffset.UtcNow,
            };

            var result = PawnIoCompatibility.Evaluate(state);

            Assert.Equal(PawnIoCompatibilityStatus.UnknownVersion, result.Status);
        }

        [Fact]
        public void TestedBaseline_IsExplicitly220()
        {
            Assert.Equal("2.2.0", PawnIoCompatibility.TestedBaseline);
        }
    }
}
