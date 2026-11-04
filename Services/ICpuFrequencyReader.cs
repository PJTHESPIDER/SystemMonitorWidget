using System;

namespace System_Monitor.Services
{
    /// <summary>
    /// Reads the live CPU clock speed using Windows performance counters.
    ///
    /// <para>
    /// The "Processor Information" category exposes the "Actual Frequency"
    /// counter (<c>AverageCount64</c>), which reports the live actual CPU
    /// frequency in MHz as a rate across all logical processors via the
    /// "_Total" instance. This counter is readable by a standard user without
    /// elevation, unlike the LHM MSR-based clock sensor which is blocked
    /// without elevation.
    ///
    /// The "Processor Frequency" and "% of Maximum Frequency" counters
    /// (both <c>NumberOfItems32</c>) are static and do not vary with the CPU's
    /// performance state, so they cannot be used to produce a live clock.
    /// </para>
    ///
    /// <para>
    /// <c>AverageCount64</c> rate counters require a priming read: the first
    /// <see cref="ReadMegahertz"/> call returns <c>null</c> and primes the
    /// counter; the subsequent call returns the live value.
    /// </para>
    /// </summary>
    public interface ICpuFrequencyReader : IDisposable
    {
        /// <summary>
        /// Reads the live CPU frequency in MHz, averaged across all logical
        /// processors via the "_Total" instance.
        /// </summary>
        /// <returns>
        /// The frequency in MHz, or null when the counter is not available
        /// (the category or counter is absent, a read fails, or the counter
        /// is still being primed). A null result means "unavailable", never
        /// "zero".
        /// </returns>
        double? ReadMegahertz();
    }
}
