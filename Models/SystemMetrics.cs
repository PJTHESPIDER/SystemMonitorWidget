using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System_Monitor.Services;

namespace System_Monitor.Models
{
    public class SystemMetrics : INotifyPropertyChanged
    {
        private double _cpuPercent;
        private double? _gpuPercent;
        private double? _gpuLoadPercent;
        private double? _integratedGpuLoadPercent;
        private double? _discreteGpuLoadPercent;
        private double? _nvidiaGpuPercent;
        private double? _cpuTemperature;
        private double? _gpuTemperature;
        private double? _acpiThermalZoneTemperatureC;
        private double _ramPercent;
        private ulong _ramUsedBytes;
        private ulong _ramTotalBytes;
        private double _diskPercent;
        private long _diskUsedBytes;
        private long _diskTotalBytes;
        private double _networkDownloadBytesPerSec;
        private double _networkUploadBytesPerSec;
        private ulong? _gpuVramUsedBytes;
        private ulong? _gpuVramTotalBytes;
        private double? _cpuClockMHz;

        public event PropertyChangedEventHandler? PropertyChanged;

        public double CpuPercent
        {
            get => _cpuPercent;
            set { _cpuPercent = value; OnPropertyChanged(nameof(CpuPercent)); }
        }

        public double? GpuPercent
        {
            get => _gpuPercent;
            set { _gpuPercent = value; OnPropertyChanged(nameof(GpuPercent)); }
        }

        /// <summary>
        /// Load of the primary (preferably discrete) GPU, as reported by a
        /// device-identified sensor backend. Null when no such sensor is
        /// readable. This is distinct from <see cref="GpuPercent"/>, which is
        /// the legacy Windows engine-counter average.
        /// </summary>
        public double? GpuLoadPercent
        {
            get => _gpuLoadPercent;
            set { _gpuLoadPercent = value; OnPropertyChanged(nameof(GpuLoadPercent)); }
        }

        /// <summary>
        /// Load of the GPU known to be integrated with the CPU, from a
        /// device-identified sensor backend. Null when no such GPU is known
        /// or its load is unreadable.
        /// </summary>
        public double? IntegratedGpuLoadPercent
        {
            get => _integratedGpuLoadPercent;
            set { _integratedGpuLoadPercent = value; OnPropertyChanged(nameof(IntegratedGpuLoadPercent)); }
        }

        /// <summary>
        /// Load of the GPU known to be discrete, from a device-identified
        /// sensor backend. Null when no such GPU is known or its load is
        /// unreadable.
        /// </summary>
        public double? DiscreteGpuLoadPercent
        {
            get => _discreteGpuLoadPercent;
            set { _discreteGpuLoadPercent = value; OnPropertyChanged(nameof(DiscreteGpuLoadPercent)); }
        }

        public double? NvidiaGpuPercent
        {
            get => _nvidiaGpuPercent;
            set { _nvidiaGpuPercent = value; OnPropertyChanged(nameof(NvidiaGpuPercent)); }
        }

        public double? CpuTemperature
        {
            get => _cpuTemperature;
            set { _cpuTemperature = value; OnPropertyChanged(nameof(CpuTemperature)); }
        }

        public double? GpuTemperature
        {
            get => _gpuTemperature;
            set { _gpuTemperature = value; OnPropertyChanged(nameof(GpuTemperature)); }
        }

        /// <summary>
        /// Temperature from unidentified ACPI thermal zones, if any sensor
        /// reports one. Not labeled as CPU or GPU temperature because the
        /// component identity is unknown.
        /// </summary>
        public double? AcpiThermalZoneTemperatureC
        {
            get => _acpiThermalZoneTemperatureC;
            set { _acpiThermalZoneTemperatureC = value; OnPropertyChanged(nameof(AcpiThermalZoneTemperatureC)); }
        }

        public double RamPercent
        {
            get => _ramPercent;
            set
            {
                _ramPercent = value;
                OnPropertyChanged(nameof(RamPercent));
                OnPropertyChanged(nameof(RamDisplay));
            }
        }

        public ulong RamUsedBytes
        {
            get => _ramUsedBytes;
            set
            {
                _ramUsedBytes = value;
                OnPropertyChanged(nameof(RamUsedBytes));
                OnPropertyChanged(nameof(RamDisplay));
            }
        }

        public ulong RamTotalBytes
        {
            get => _ramTotalBytes;
            set
            {
                _ramTotalBytes = value;
                OnPropertyChanged(nameof(RamTotalBytes));
                OnPropertyChanged(nameof(RamDisplay));
            }
        }

        public string RamDisplay => ByteFormatter.FormatUsage(RamUsedBytes, RamTotalBytes, RamPercent);

        public double DiskPercent
        {
            get => _diskPercent;
            set
            {
                _diskPercent = value;
                OnPropertyChanged(nameof(DiskPercent));
                OnPropertyChanged(nameof(DiskDisplay));
            }
        }

        public long DiskUsedBytes
        {
            get => _diskUsedBytes;
            set
            {
                _diskUsedBytes = value;
                OnPropertyChanged(nameof(DiskUsedBytes));
                OnPropertyChanged(nameof(DiskDisplay));
            }
        }

        public long DiskTotalBytes
        {
            get => _diskTotalBytes;
            set
            {
                _diskTotalBytes = value;
                OnPropertyChanged(nameof(DiskTotalBytes));
                OnPropertyChanged(nameof(DiskDisplay));
            }
        }

        public string DiskDisplay => ByteFormatter.FormatUsage(Math.Max(0, DiskUsedBytes), Math.Max(0, DiskTotalBytes), DiskPercent);

        public double NetworkDownloadBytesPerSec
        {
            get => _networkDownloadBytesPerSec;
            set { _networkDownloadBytesPerSec = value; OnPropertyChanged(nameof(NetworkDownloadBytesPerSec)); }
        }

        public double NetworkUploadBytesPerSec
        {
            get => _networkUploadBytesPerSec;
            set { _networkUploadBytesPerSec = value; OnPropertyChanged(nameof(NetworkUploadBytesPerSec)); }
        }

        public ulong? GpuVramUsedBytes
        {
            get => _gpuVramUsedBytes;
            set
            {
                _gpuVramUsedBytes = value;
                OnPropertyChanged(nameof(GpuVramUsedBytes));
                OnPropertyChanged(nameof(GpuVramDisplay));
            }
        }

        public ulong? GpuVramTotalBytes
        {
            get => _gpuVramTotalBytes;
            set
            {
                _gpuVramTotalBytes = value;
                OnPropertyChanged(nameof(GpuVramTotalBytes));
                OnPropertyChanged(nameof(GpuVramDisplay));
            }
        }

        public string? GpuVramDisplay
        {
            get
            {
                if (!_gpuVramUsedBytes.HasValue) return null;
                if (_gpuVramTotalBytes.HasValue && _gpuVramTotalBytes.Value > 0)
                {
                    var pct = (double)_gpuVramUsedBytes.Value / _gpuVramTotalBytes.Value * 100.0;
                    return ByteFormatter.FormatUsage(_gpuVramUsedBytes.Value, _gpuVramTotalBytes.Value, pct);
                }
                return ByteFormatter.FormatBytes(_gpuVramUsedBytes.Value);
            }
        }

        public double? CpuClockMHz
        {
            get => _cpuClockMHz;
            set
            {
                _cpuClockMHz = value;
                OnPropertyChanged(nameof(CpuClockMHz));
                OnPropertyChanged(nameof(CpuClockDisplay));
            }
        }

        public string CpuClockDisplay
        {
            get
            {
                if (!_cpuClockMHz.HasValue) return "N/A";
                var mhz = _cpuClockMHz.Value;
                return mhz >= 1000 ? $"{mhz / 1000.0:F2} GHz" : $"{mhz:F0} MHz";
            }
        }


        // Chart series (simple rolling collections)
        public ObservableCollection<double> CpuSeries { get; } = new ObservableCollection<double>();
        public ObservableCollection<double> RamSeries { get; } = new ObservableCollection<double>();
        public ObservableCollection<double> DiskSeries { get; } = new ObservableCollection<double>();
        public ObservableCollection<double> NetworkDownloadSeries { get; } = new ObservableCollection<double>();
        public ObservableCollection<double> NetworkUploadSeries { get; } = new ObservableCollection<double>();

        protected void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}