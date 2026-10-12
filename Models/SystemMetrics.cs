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
        private double? _nvidiaGpuPercent;
        private double _ramPercent;
        private ulong _ramUsedBytes;
        private ulong _ramTotalBytes;
        private double _diskPercent;
        private long _diskUsedBytes;
        private long _diskTotalBytes;
        private double _networkDownloadBytesPerSec;
        private double _networkUploadBytesPerSec;

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

        public double? NvidiaGpuPercent
        {
            get => _nvidiaGpuPercent;
            set { _nvidiaGpuPercent = value; OnPropertyChanged(nameof(NvidiaGpuPercent)); }
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