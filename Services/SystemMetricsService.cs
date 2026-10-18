using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    public sealed class SystemMetricsService : IDisposable
    {
        private readonly SystemMetrics _model;
        private readonly WidgetSettings _settings;
        private Timer _timer;
        private TimeSpan _interval;
        private readonly GpuService _gpuService = new GpuService();
        private ulong _prevIdle;
        private ulong _prevKernel;
        private ulong _prevUser;
        private long _prevNetworkBytesReceived;
        private long _prevNetworkBytesSent;
        private PerformanceCounter? _diskReadBytesPerSec;
        private PerformanceCounter? _diskWriteBytesPerSec;

        public SystemMetricsService(SystemMetrics model, WidgetSettings settings)
        {
            _model = model;
            _settings = settings;
            _interval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds);

            // Prepare performance counters for disk IO (may throw on systems without counters)
            try
            {
                _diskReadBytesPerSec = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                _diskWriteBytesPerSec = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
            }
            catch
            {
                _diskReadBytesPerSec = null;
                _diskWriteBytesPerSec = null;
            }

            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                _prevIdle = FileTimeToUInt64(idle);
                _prevKernel = FileTimeToUInt64(kernel);
                _prevUser = FileTimeToUInt64(user);
            }

            var (rx, tx) = GetTotalNetworkBytes();
            _prevNetworkBytesReceived = rx;
            _prevNetworkBytesSent = tx;

            _timer = new Timer(OnTimer, null, TimeSpan.Zero, _interval);

            // react to settings changes (interval)
            _settings.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(WidgetSettings.RefreshIntervalSeconds))
                {
                    var newInterval = TimeSpan.FromSeconds(_settings.RefreshIntervalSeconds);
                    _interval = newInterval;
                    _timer.Change(TimeSpan.Zero, _interval);
                }
            };
        }

        public void Dispose()
        {
            _timer?.Dispose();
            _diskReadBytesPerSec?.Dispose();
            _diskWriteBytesPerSec?.Dispose();
        }

        private static void Dispatch(Action action)
        {
            action();
        }

        private void OnTimer(object? state)
        {
            try
            {
                if (_settings.ShowCpu) UpdateCpu();
                if (_settings.ShowRam) UpdateRam();
                if (_settings.ShowDisk) UpdateDisk();
                if (_settings.ShowNetwork) UpdateNetwork();
                if (_settings.ShowGpu) UpdateGpuBestEffort();
                if (_settings.ShowCpuTemp) UpdateCpuTemp();
                if (_settings.ShowGpuTemp) UpdateGpuTemp();
                if (_settings.ShowGpuVram) UpdateGpuVram();
                if (_settings.ShowCpuClock) UpdateCpuClock();
            }
            catch
            {
                // keep timer running
            }
        }

        private void UpdateCpu()
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user))
                return;

            var idle64 = FileTimeToUInt64(idle);
            var kernel64 = FileTimeToUInt64(kernel);
            var user64 = FileTimeToUInt64(user);

            var sys = (kernel64 - _prevKernel) + (user64 - _prevUser);
            var idleDiff = idle64 - _prevIdle;

            if (sys > 0)
            {
                var usage = (1.0 - (double)idleDiff / sys) * 100.0;
                var clamped = Math.Max(0, Math.Min(100, usage));

                // Marshal updates to dispatcher (if present) or invoke directly
                Dispatch(() =>
                {
                    UpdateSeries(_model.CpuSeries, usage);
                    _model.CpuPercent = clamped;
                });
            }

            _prevIdle = idle64;
            _prevKernel = kernel64;
            _prevUser = user64;
        }

        private void UpdateRam()
        {
            var mem = new MEMORYSTATUSEX();
            mem.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
            if (GlobalMemoryStatusEx(ref mem))
            {
                var used = mem.ullTotalPhys - mem.ullAvailPhys;
                var percent = (double)used / mem.ullTotalPhys * 100.0;
                var clamped = Math.Max(0, Math.Min(100, percent));

                Dispatch(() =>
                {
                    UpdateSeries(_model.RamSeries, percent);
                    _model.RamUsedBytes = used;
                    _model.RamTotalBytes = mem.ullTotalPhys;
                    _model.RamPercent = clamped;
                });
            }
        }

        private void UpdateDisk()
        {
            try
            {
                // Use primary system drive for used percentage
                var systemDrive = Environment.GetFolderPath(Environment.SpecialFolder.System).Substring(0, 3);
                var drive = new System.IO.DriveInfo(systemDrive);
                if (drive.IsReady)
                {
                    var used = (drive.TotalSize - drive.TotalFreeSpace);
                    var percent = (double)used / drive.TotalSize * 100.0;
                    var clamped = Math.Max(0, Math.Min(100, percent));

                    Dispatch(() =>
                    {
                        UpdateSeries(_model.DiskSeries, percent);
                        _model.DiskUsedBytes = used;
                        _model.DiskTotalBytes = drive.TotalSize;
                        _model.DiskPercent = clamped;
                    });
                }
            }
            catch { }
        }

        private void UpdateNetwork()
        {
            var (rx, tx) = GetTotalNetworkBytes();
            var deltaRx = rx - _prevNetworkBytesReceived;
            var deltaTx = tx - _prevNetworkBytesSent;
            var seconds = _interval.TotalSeconds;
            var rxPerSec = deltaRx / seconds;
            var txPerSec = deltaTx / seconds;

            Dispatch(() =>
            {
                UpdateSeries(_model.NetworkDownloadSeries, rxPerSec);
                UpdateSeries(_model.NetworkUploadSeries, txPerSec);

                _model.NetworkDownloadBytesPerSec = rxPerSec;
                _model.NetworkUploadBytesPerSec = txPerSec;
            });

            _prevNetworkBytesReceived = rx;
            _prevNetworkBytesSent = tx;
        }

        private void UpdateGpuBestEffort()
        {
            try
            {
                var gpuPercent = _gpuService.GetGpuPercentBestEffort();
                var nvidiaGpuPercent = _gpuService.GetNvidiaGpuPercent();
                Dispatch(() => 
                {
                    _model.GpuPercent = gpuPercent;
                    _model.NvidiaGpuPercent = nvidiaGpuPercent;
                });
            }
            catch
            {
                // ignore
            }
        }

        private void UpdateCpuTemp()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
                var collection = searcher.Get();
                var temps = collection.Cast<ManagementObject>()
                    .Select(mo => TryGetDouble(mo, "CurrentTemperature"))
                    .Where(v => v.HasValue)
                    .Select(v => (v!.Value - 273.2) / 10.0)
                    .Where(t => t > 0 && t < 200)
                    .ToList();
                if (temps.Count > 0)
                {
                    Dispatch(() => _model.CpuTemperature = temps.Average());
                }
            }
            catch { }
        }

        private void UpdateGpuTemp()
        {
            try
            {
                var temp = _gpuService.GetGpuTemperatureBestEffort();
                Dispatch(() => _model.GpuTemperature = temp);
            }
            catch { }
        }

        private void UpdateGpuVram()
        {
            try
            {
                var (used, total) = _gpuService.GetGpuVramBestEffort();
                Dispatch(() =>
                {
                    _model.GpuVramUsedBytes = used;
                    _model.GpuVramTotalBytes = total;
                });
            }
            catch { }
        }

        private void UpdateCpuClock()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                    "SELECT CurrentClockSpeed FROM Win32_Processor");
                var speeds = searcher.Get()
                    .Cast<ManagementObject>()
                    .Select(mo => TryGetDouble(mo, "CurrentClockSpeed"))
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .ToList();
                if (speeds.Count > 0)
                {
                    Dispatch(() => _model.CpuClockMHz = speeds.Average());
                }
            }
            catch { }
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

        private static void UpdateSeries(System.Collections.ObjectModel.ObservableCollection<double> series, double value, int maxPoints = 30)
        {
            // Keep values reasonable for charting (e.g., clamp for visual stability)
            if (double.IsNaN(value) || double.IsInfinity(value))
                value = 0;
            lock (series)
            {
                series.Add(value);
                while (series.Count > maxPoints)
                    series.RemoveAt(0);
            }
        }

        private static (long rx, long tx) GetTotalNetworkBytes()
        {
            long rx = 0, tx = 0;
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;
                    var stats = ni.GetIPv4Statistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                }
            }
            catch { }
            return (rx, tx);
        }

        #region PInvoke and helpers

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetSystemTimes")]
        private static extern bool GetSystemTimesNative(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        private static bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user)
        {
            return GetSystemTimesNative(out idle, out kernel, out user);
        }

        private static ulong FileTimeToUInt64(FILETIME ft)
        {
            return ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        #endregion
    }
}