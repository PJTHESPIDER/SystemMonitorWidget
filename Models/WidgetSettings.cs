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