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
    }
}