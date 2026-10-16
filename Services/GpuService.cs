using System;
using System.Linq;
using System.Management;
using System.Diagnostics;

namespace System_Monitor.Services
{
    public sealed class GpuService
    {
        // Attempts WMI and Performance Counter methods for GPU usage estimation.
        public double? GetGpuPercentBestEffort()
        {
            return GetIntelGpuPercentBestEffort();
        }

        public double? GetGpuTemperatureBestEffort()
        {
            var nvidiaTemp = GetNvidiaGpuTemperature();
            if (nvidiaTemp.HasValue)
                return nvidiaTemp;

            return GetIntelGpuTemperatureBestEffort();
        }

        /// <summary>
        /// Returns (usedBytes, totalBytes). Either may be null if unavailable.
        /// Tries nvidia-smi first, then WMI performance counters.
        /// </summary>
        public (ulong? usedBytes, ulong? totalBytes) GetGpuVramBestEffort()
        {
            // NVIDIA via nvidia-smi
            var nvidia = GetNvidiaGpuVram();
            if (nvidia.usedBytes.HasValue || nvidia.totalBytes.HasValue)
                return nvidia;

            // Fallback: WMI GPU local video memory counters
            return GetWmiGpuVram();
        }

        private (ulong? usedBytes, ulong? totalBytes) GetNvidiaGpuVram()
        {
            try
            {
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = @"C:\Windows\System32\nvidia-smi.exe",
                        Arguments = "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();

                // Output: "1234, 8192"  (MiB values)
                var parts = output.Split(',');
                if (parts.Length >= 2
                    && ulong.TryParse(parts[0].Trim(), out var usedMiB)
                    && ulong.TryParse(parts[1].Trim(), out var totalMiB))
                {
                    return (usedMiB * 1024 * 1024, totalMiB * 1024 * 1024);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to get NVIDIA VRAM: {ex.Message}");
            }
            return (null, null);
        }

        private (ulong? usedBytes, ulong? totalBytes) GetWmiGpuVram()
        {
            ulong? usedBytes = null;
            ulong? totalBytes = null;
            try
            {
                // Try dedicated video memory usage counter
                using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                    "SELECT * FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPULocalVideoMemory");
                var col = searcher.Get();
                ulong totalUsed = 0; ulong totalCapacity = 0; bool any = false;
                foreach (ManagementObject mo in col)
                {
                    if (mo["CurrentUsage"] is ulong used) { totalUsed += used; any = true; }
                    if (mo["AvailableUsage"] is ulong avail && mo["CurrentUsage"] is ulong u2)
                        totalCapacity += u2 + avail;
                }
                if (any)
                {
                    usedBytes = totalUsed * 1024; // counter is in KB
                    if (totalCapacity > 0) totalBytes = totalCapacity * 1024;
                }
            }
            catch { }

            // Fallback: Win32_VideoController for total only
            if (!totalBytes.HasValue)
            {
                try
                {
                    using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                        "SELECT AdapterRAM FROM Win32_VideoController");
                    foreach (ManagementObject mo in searcher.Get())
                    {
                        if (mo["AdapterRAM"] is uint ram && ram > 0)
                        {
                            totalBytes = ram;
                            break;
                        }
                    }
                }
                catch { }
            }

            return (usedBytes, totalBytes);
        }


        public double? GetNvidiaGpuPercent()
        {
            try
            {
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = @"C:\Windows\System32\nvidia-smi.exe",
                        Arguments = "--query-gpu=utilization.gpu --format=csv,noheader,nounits",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                
                if (double.TryParse(output.Trim(), out var percent))
                {
                    return percent;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to get NVIDIA GPU percent: {ex.Message}");
            }
            return null;
        }

        public double? GetIntelGpuPercentBestEffort()
        {
            // 1) Try WMI class or Performance counters (best-effort)
            try
            {
                // Try Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine
                using var searcher = new ManagementObjectSearcher("root\\CIMV2", "SELECT * FROM Win32_PerfFormattedData_GPUPerformanceCounters_GPUEngine");
                var collection = searcher.Get();
                var values = collection.Cast<ManagementObject>()
                    .SelectMany(mo => new[] { TryGetDouble(mo, "UtilizationPercentage"), TryGetDouble(mo, "PercentGPUUtilization"), TryGetDouble(mo, "Utilization") })
                    .Where(v => v.HasValue).Select(v => v!.Value).ToList();
                if (values.Count > 0)
                {
                    return values.Average();
                }
            }
            catch
            {
                // ignore WMI errors
            }

            // 3) Try PerformanceCounter category "GPU Engine" if available
            try
            {
                // Instances like "pid_####_engtype_3D" expose "Utilization Percentage" or "% Utilization"
                // Aggregate across instances
                double sum = 0;
                int count = 0;
                var catNames = new[] { "GPU Engine", "GPU" };
                foreach (var cat in catNames)
                {
                    try
                    {
                        if (!PerformanceCounterCategory.Exists(cat)) continue;
                        var pcc = new PerformanceCounterCategory(cat);
                        var instances = pcc.GetInstanceNames();
                        foreach (var inst in instances)
                        {
                            try
                            {
                                using var pc = new PerformanceCounter(cat, "Utilization Percentage", inst, readOnly: true);
                                var val = pc.NextValue();
                                if (!double.IsNaN(val) && !double.IsInfinity(val))
                                { sum += val; count++; }
                            }
                            catch { }
                        }
                        if (count > 0) return sum / count;
                    }
                    catch { }
                }
            }
            catch { }

            // Nothing available
            return null;
        }

        private static double? TryGetDouble(ManagementObject mo, string prop)
        {
            try
            {
                if (mo[prop] != null && double.TryParse(mo[prop].ToString(), out var v))
                    return v;
            }
            catch { }
            return null;
        }

        private double? GetNvidiaGpuTemperature()
        {
            try
            {
                var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = @"C:\Windows\System32\nvidia-smi.exe",
                        Arguments = "--query-gpu=temperature.gpu --format=csv,noheader,nounits",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();

                if (double.TryParse(output.Trim(), out var temp))
                    return temp;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to get NVIDIA GPU temperature: {ex.Message}");
            }
            return null;
        }

        private double? GetIntelGpuTemperatureBestEffort()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
                var collection = searcher.Get();
                var temps = collection.Cast<ManagementObject>()
                    .Select(mo => TryGetDouble(mo, "CurrentTemperature"))
                    .Where(v => v.HasValue)
                    .Select(v => (v!.Value - 273.2) / 10.0) // WMI reports tenths of Kelvin
                    .Where(t => t > 0 && t < 200)
                    .ToList();
                if (temps.Count > 0)
                    return temps.Average();
            }
            catch { }
            return null;
        }
    }
}