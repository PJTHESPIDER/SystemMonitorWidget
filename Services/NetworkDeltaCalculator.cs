using System;

namespace System_Monitor.Services
{
    /// <summary>
    /// A single network-interface sample used as a baseline for delta
    /// calculation. Contains no hardware dependencies.
    /// </summary>
    public readonly record struct InterfaceSample(string InterfaceId, long BytesReceived, long BytesSent, DateTimeOffset Timestamp);

    /// <summary>
    /// Result of a network-delta calculation.
    /// </summary>
    public readonly record struct DeltaResult(double BytesReceivedPerSec, double BytesSentPerSec, bool HasValidDelta);

    /// <summary>
    /// Pure network-delta logic. Given current interface counters and an
    /// optional previous sample, computes bytes-per-second rates using the
    /// actual elapsed time. Detects counter resets and missing baselines
    /// without emitting negative or spike rates.
    /// </summary>
    public sealed class NetworkDeltaCalculator
    {
        /// <summary>
        /// Calculates the per-second receive/transmit rates for one interface.
        /// </summary>
        /// <param name="interfaceId">Stable interface identifier.</param>
        /// <param name="currentRx">Current bytes received.</param>
        /// <param name="currentTx">Current bytes sent.</param>
        /// <param name="now">Current timestamp (monotonic or wall-clock).</param>
        /// <param name="previous">Previous sample for this interface, if any.</param>
        /// <returns>Rates and a flag indicating whether a valid delta was computed.</returns>
        public DeltaResult Calculate(string interfaceId, long currentRx, long currentTx, DateTimeOffset now,
            InterfaceSample? previous)
        {
            // No baseline yet: establish one, emit no rate.
            if (previous is null)
                return new DeltaResult(0.0, 0.0, false);

            var elapsedSeconds = (now - previous.Value.Timestamp).TotalSeconds;
            if (elapsedSeconds <= 0.0)
                return new DeltaResult(0.0, 0.0, false);

            long deltaRx = currentRx - previous.Value.BytesReceived;
            long deltaTx = currentTx - previous.Value.BytesSent;

            // Counter reset or wrap: new value is less than the baseline.
            // Do not emit a negative rate; signal that a rebaseline is needed.
            if (deltaRx < 0 || deltaTx < 0)
                return new DeltaResult(0.0, 0.0, false);

            return new DeltaResult(deltaRx / elapsedSeconds, deltaTx / elapsedSeconds, true);
        }
    }
}
