using System;
using System.Collections.Generic;

namespace System_Monitor.Models
{
    /// <summary>
    /// The outcome of reinitializing the sensor backend after a driver
    /// installation or manual re-verify. Structured so the caller gets
    /// explicit states rather than exceptions.
    /// </summary>
    public enum ReinitializationOutcome
    {
        /// <summary>Backend reinitialized successfully.</summary>
        Success,

        /// <summary>Backend could not be reinitialized — initialization failed.</summary>
        InitializationFailed,

        /// <summary>Backend initialized but CPU temperature sensors are unavailable.</summary>
        TemperatureUnavailable,

        /// <summary>The source was disposed during reinitialization.</summary>
        DisposedDuringReinit,

        /// <summary>The operation was cancelled.</summary>
        Cancelled,

        /// <summary>Reinitialization was not performed (e.g., not previously initialized).</summary>
        NotInitialized,
    }

    /// <summary>
    /// Structured result of reinitializing the sensor backend.
    /// No exceptions for expected outcomes — the caller inspects the outcome.
    /// </summary>
    public sealed record ReinitializeResult
    {
        /// <summary>The outcome of reinitialization.</summary>
        public ReinitializationOutcome Outcome { get; init; }

        /// <summary>True when the backend is available after reinit.</summary>
        public bool WasAvailableBefore { get; init; }

        /// <summary>Names of CPU temperature sensors that became available after reinit.</summary>
        public IReadOnlyList<string> NewlyAvailableSensorTypes { get; init; } = Array.Empty<string>();

        /// <summary>Reason for failure when Outcome is not Success.</summary>
        public string? FailureReason { get; init; }

        /// <summary>When the reinitialization completed.</summary>
        public DateTimeOffset CompletedAt { get; init; }
    }
}
