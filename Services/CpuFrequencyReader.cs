using System;
using System.Diagnostics;

namespace System_Monitor.Services
{
    /// <summary>
    /// Reads the live CPU clock speed using the Windows "Processor Information"
    /// "Actual Frequency" performance counter, which is accessible to a
    /// standard user without elevation.
    ///
    /// <para>
    /// "Actual Frequency" (CounterType <c>AverageCount64</c>) is a rate counter
    /// that reports the live actual CPU frequency in MHz, averaged across all
    /// logical processors via the "_Total" instance. Unlike the "Processor
    /// Frequency" and "% of Maximum Frequency" counters (both
    /// <c>NumberOfItems32</c> — static values that do not vary), "Actual
    /// Frequency" varies with the CPU's performance state and reflects turbo
    /// boost and power management.
    ///
    /// Previously the reader computed frequency as
    /// <c>Processor Frequency * % of Maximum Frequency / 100</c>, but on
    /// this hardware both inputs are static (2304 and 100 respectively),
    /// yielding a constant 2304 MHz regardless of load. "Actual Frequency"
    /// is the correct live source.
    /// </para>
    ///
    /// <para>
    /// <c>AverageCount64</c> rate counters require two samples separated by a
    /// time delta: the first <c>NextValue()</c> call primes the counter, and
    /// the second returns the computed rate. This class keeps priming state
    /// across calls so that no blocking sleep is needed within a single
    /// <see cref="ReadMegahertz"/> call. On the priming read the method
    /// returns <c>null</c> (meaning "unavailable for this cycle"), and the
    /// next scheduled collection supplies the live value.
    /// </para>
    ///
    /// <para>
    /// The "_Total" instance averages across all logical processors, matching
    /// the intent of the old WMI averaging across Win32_Processor instances.
    /// </para>
    /// </summary>
    public sealed class CpuFrequencyReader : ICpuFrequencyReader
    {
        private readonly PerformanceCounter _actualFreqCounter;
        private bool _disposed;

        // True after the first NextValue() call, which primes the counter.
        // Subsequent calls return a live rate.
        private bool _isPrimed;

        public CpuFrequencyReader()
        {
            _actualFreqCounter = new PerformanceCounter(
                "Processor Information", "Actual Frequency", "_Total");
            _isPrimed = false;
        }

        /// <summary>
        /// Reads the live CPU frequency in MHz.
        ///
        /// <para>
        /// "Actual Frequency" is an <c>AverageCount64</c> rate counter that
        /// requires a priming read followed by a second read after a time
        /// delta. The priming state is maintained across calls so no blocking
        /// sleep is needed. On the first call (priming), returns <c>null</c>;
        /// on the next call the live frequency is returned.
        /// </para>
        ///
        /// <para>
        /// Returns <c>null</c> when the counter reports a non-positive or
        /// non-finite value, so a cold or intermittent reading is treated as
        /// unavailable rather than as zero or an invalid number.
        /// </para>
        /// </summary>
        public double? ReadMegahertz()
        {
            if (_disposed)
                throw new ObjectDisposedException(GetType().FullName);

            try
            {
                if (!_isPrimed)
                {
                    // Prime the rate counter. The first NextValue() returns 0
                    // and discards the result. The next call returns the rate.
                    _actualFreqCounter.NextValue();
                    _isPrimed = true;
                    return null;
                }

                var freq = _actualFreqCounter.NextValue();

                // Validate: must be finite and positive to be used.
                if (!double.IsFinite(freq) || freq <= 0)
                {
                    // Reset priming state so the next call re-primes.
                    _isPrimed = false;
                    return null;
                }

                return freq;
            }
            catch (Exception)
            {
                // Category or counter unavailable, access denied, etc.
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            _actualFreqCounter.Dispose();
        }
    }
}
