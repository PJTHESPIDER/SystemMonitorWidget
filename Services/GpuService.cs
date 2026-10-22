using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;

namespace System_Monitor.Services
{
    /// <summary>
    /// Vendor-neutral GPU metrics read from the Windows performance
    /// counters.
    ///
    /// <para>
    /// The cross-vendor sensor backend (LibreHardwareMonitor) is the
    /// primary source for GPU temperature and VRAM. The Windows counters
    /// are the source for the dedicated-memory figures the backend does
    /// not always expose.
    /// </para>
    ///
    /// <para>
    /// The Windows counters are not device-identified. They report one
    /// figure per adapter, keyed by an LUID, and the widget may be
    /// presenting any one of several adapters. A figure is therefore only
    /// used when the counters described exactly one adapter; otherwise it
    /// is ambiguous, and an ambiguous figure is reported as unavailable
    /// rather than attributed to a device it may not describe. See
    /// <see cref="GpuMemoryFallbackPolicy"/>.
    /// </para>
    ///
    /// <para>
    /// The NVIDIA command-line tool is consulted only for a metric the
    /// primary source could not supply. See <see cref="NvidiaGpuFallback"/>
    /// for what "optional fallback" means in practice.
    /// </para>
    /// </summary>
    public sealed class GpuService
    {
        /// <summary>
        /// The backend name used in the diagnostic log.
        /// </summary>
        public const string BackendName = "gpu-counters";

        /// <summary>
        /// Returns (usedBytes, totalBytes). Either may be null if unavailable.
        ///
        /// The caller is responsible for preferring the device-identified
        /// backend's value; this method is the fallback path, which uses the
        /// Windows GPU memory counters.
        /// </summary>
        public (ulong? usedBytes, ulong? totalBytes) GetGpuVramBestEffort()
        {
            return GetWmiGpuVram();
        }

        /// <summary>
        /// Reads the dedicated video memory usage from the Windows GPU
        /// adapter-memory counters.
        ///
        /// <para>
        /// The counters report one instance per adapter. A figure is only
        /// used when exactly one adapter was reported, because otherwise
        /// there is no way to know which adapter the figure describes.
        /// Summing the used figures across adapters would report the
        /// machine's total GPU memory in use as one card's usage; taking
        /// the first adapter's capacity would report the integrated part's
        /// capacity as the discrete card's. Both misattribute another
        /// device's reading, so an ambiguous reading is reported as
        /// unavailable instead.
        /// </para>
        /// </summary>
        private (ulong? usedBytes, ulong? totalBytes) GetWmiGpuVram()
        {
            var readings = new GpuMemoryCounterReadings(
                Array.Empty<GpuAdapterMemorySample>(),
                Array.Empty<ulong>());

            var started = Stopwatch.StartNew();

            try
            {
                // Try dedicated video memory usage counter
                using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                    "SELECT * FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory");
                var col = searcher.Get();
                var samples = new List<GpuAdapterMemorySample>();
                foreach (ManagementObject mo in col)
                {
                    if (mo["DedicatedUsage"] is ulong used)
                    {
                        var name = mo["Name"] as string ?? string.Empty;
                        samples.Add(new GpuAdapterMemorySample(name, used));
                    }
                }

                readings = readings with { UsedBytes = samples };
            }
            catch (Exception ex)
            {
                DiagnosticLog.Shared.Warning(
                    BackendName,
                    "vram-used",
                    "unavailable",
                    elapsed: started.Elapsed,
                    detail: DiagnosticDetail.Describe(ex));
            }

            // Fallback: Win32_VideoController for total only
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                    "SELECT AdapterRAM FROM Win32_VideoController");
                var capacities = new List<ulong>();
                foreach (ManagementObject mo in searcher.Get())
                {
                    if (mo["AdapterRAM"] is uint ram && ram > 0)
                    {
                        capacities.Add(ram);
                    }
                }

                readings = readings with { TotalBytes = capacities };
            }
            catch (Exception ex)
            {
                DiagnosticLog.Shared.Warning(
                    BackendName,
                    "vram-total",
                    "unavailable",
                    elapsed: started.Elapsed,
                    detail: DiagnosticDetail.Describe(ex));
            }

            return GpuMemoryFallbackPolicy.Resolve(readings);
        }

    }
}
