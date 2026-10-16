using System;
using System.ComponentModel;

namespace System_Monitor.Models
{
    public class WidgetSettings : INotifyPropertyChanged
    {
        private bool _showCpu = true;
        private bool _showGpu = true;
        private bool _showRam = true;
        private bool _showDisk = true;
        private bool _showNetwork = true;
        private bool _showCpuTemp = true;
        private bool _showGpuTemp = true;
        private bool _showGpuVram = true;
        private bool _showCpuClock = true;
        private int _refreshIntervalSeconds = 5;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool ShowCpu
        {
            get => _showCpu;
            set { if (_showCpu != value) { _showCpu = value; OnPropertyChanged(nameof(ShowCpu)); } }
        }

        public bool ShowGpu
        {
            get => _showGpu;
            set { if (_showGpu != value) { _showGpu = value; OnPropertyChanged(nameof(ShowGpu)); } }
        }

        public bool ShowRam
        {
            get => _showRam;
            set { if (_showRam != value) { _showRam = value; OnPropertyChanged(nameof(ShowRam)); } }
        }

        public bool ShowDisk
        {
            get => _showDisk;
            set { if (_showDisk != value) { _showDisk = value; OnPropertyChanged(nameof(ShowDisk)); } }
        }

        public bool ShowNetwork
        {
            get => _showNetwork;
            set { if (_showNetwork != value) { _showNetwork = value; OnPropertyChanged(nameof(ShowNetwork)); } }
        }

        public bool ShowCpuTemp
        {
            get => _showCpuTemp;
            set { if (_showCpuTemp != value) { _showCpuTemp = value; OnPropertyChanged(nameof(ShowCpuTemp)); } }
        }

        public bool ShowGpuTemp
        {
            get => _showGpuTemp;
            set { if (_showGpuTemp != value) { _showGpuTemp = value; OnPropertyChanged(nameof(ShowGpuTemp)); } }
        }

        public bool ShowGpuVram
        {
            get => _showGpuVram;
            set { if (_showGpuVram != value) { _showGpuVram = value; OnPropertyChanged(nameof(ShowGpuVram)); } }
        }

        public bool ShowCpuClock
        {
            get => _showCpuClock;
            set { if (_showCpuClock != value) { _showCpuClock = value; OnPropertyChanged(nameof(ShowCpuClock)); } }
        }

        public int RefreshIntervalSeconds
        {
            get => _refreshIntervalSeconds;
            set
            {
                if (value < 1) value = 1;
                if (_refreshIntervalSeconds != value)
                {
                    _refreshIntervalSeconds = value;
                    OnPropertyChanged(nameof(RefreshIntervalSeconds));
                }
            }
        }

        private void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}