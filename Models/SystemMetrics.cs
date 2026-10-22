using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace System_Monitor.Models
{
    /// <summary>
    /// The mutable view of the machine's current metrics.
    ///
    /// <para><b>What this type is for</b></para>
    ///
    /// <para>
    /// The widget is served from an immutable <see cref="MetricsSnapshot"/>,
    /// which is the authoritative record of a collection. This model is the
    /// working view the collectors write into between collections, and it
    /// exists so a collector can read back what another collector found.
    /// </para>
    ///
    /// <para><b>Notifications</b></para>
    ///
    /// <para>
    /// A notification is raised only when the value actually changed. The
    /// collectors write on every cycle — every five seconds — and most of
    /// those writes do not change the value: the RAM total is constant, the
    /// disk size is constant, the VRAM total is constant. Raising a
    /// notification for an unchanged value costs a comparison, an event
    /// dispatch and a string allocation, and buys nothing, because a
    /// listener that re-reads an unchanged value has nothing to redraw.
    /// </para>
    ///
    /// <para><b>Formatting</b></para>
    ///
    /// <para>
    /// This type holds measurements, not formatted text. The display
    /// strings live with the presenters that produce them
    /// (<see cref="Services.ByteFormatter"/>,
    /// <see cref="Services.WidgetPayloadPresenter"/>), so the formatting
    /// rules are stated once and the model stays a model.
    /// </para>
    /// </summary>
    public class SystemMetrics : INotifyPropertyChanged
    {
        private double _cpuPercent;
        private double? _gpuLoadPercent;
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

        /// <summary>
        /// The CPU load, as a percentage.
        /// </summary>
        public double CpuPercent
        {
            get => _cpuPercent;
            set => Set(ref _cpuPercent, value);
        }

        /// <summary>
        /// Load of the primary (preferably discrete) GPU, as reported by a
        /// device-identified sensor backend. Null when no such sensor is
        /// readable. This is the device-identified figure; the Windows
        /// engine-counter average is not device-attributed and is not
        /// presented.
        /// </summary>
        public double? GpuLoadPercent
        {
            get => _gpuLoadPercent;
            set => Set(ref _gpuLoadPercent, value);
        }

        /// <summary>
        /// CPU temperature in degrees Celsius. Null when the sensor is not
        /// readable.
        /// </summary>
        public double? CpuTemperature
        {
            get => _cpuTemperature;
            set => Set(ref _cpuTemperature, value);
        }

        /// <summary>
        /// GPU temperature in degrees Celsius. Null when the sensor is not
        /// readable.
        /// </summary>
        public double? GpuTemperature
        {
            get => _gpuTemperature;
            set => Set(ref _gpuTemperature, value);
        }

        /// <summary>
        /// Temperature from unidentified ACPI thermal zones, if any sensor
        /// reports one. Not labeled as CPU or GPU temperature because the
        /// component identity is unknown.
        /// </summary>
        public double? AcpiThermalZoneTemperatureC
        {
            get => _acpiThermalZoneTemperatureC;
            set => Set(ref _acpiThermalZoneTemperatureC, value);
        }

        /// <summary>
        /// Used physical memory, in bytes.
        /// </summary>
        public ulong RamUsedBytes
        {
            get => _ramUsedBytes;
            set => Set(ref _ramUsedBytes, value);
        }

        /// <summary>
        /// Total physical memory, in bytes.
        /// </summary>
        public ulong RamTotalBytes
        {
            get => _ramTotalBytes;
            set => Set(ref _ramTotalBytes, value);
        }

        /// <summary>
        /// Used physical memory as a percentage of the total.
        /// </summary>
        public double RamPercent
        {
            get => _ramPercent;
            set => Set(ref _ramPercent, value);
        }

        /// <summary>
        /// Used space on the system drive, in bytes.
        /// </summary>
        public long DiskUsedBytes
        {
            get => _diskUsedBytes;
            set => Set(ref _diskUsedBytes, value);
        }

        /// <summary>
        /// Total space on the system drive, in bytes.
        /// </summary>
        public long DiskTotalBytes
        {
            get => _diskTotalBytes;
            set => Set(ref _diskTotalBytes, value);
        }

        /// <summary>
        /// Used space on the system drive as a percentage of the total.
        /// </summary>
        public double DiskPercent
        {
            get => _diskPercent;
            set => Set(ref _diskPercent, value);
        }

        /// <summary>
        /// Download throughput across all included interfaces, in bytes
        /// per second.
        /// </summary>
        public double NetworkDownloadBytesPerSec
        {
            get => _networkDownloadBytesPerSec;
            set => Set(ref _networkDownloadBytesPerSec, value);
        }

        /// <summary>
        /// Upload throughput across all included interfaces, in bytes per
        /// second.
        /// </summary>
        public double NetworkUploadBytesPerSec
        {
            get => _networkUploadBytesPerSec;
            set => Set(ref _networkUploadBytesPerSec, value);
        }

        /// <summary>
        /// Dedicated GPU memory in use, in bytes. Null when the figure is
        /// not readable.
        /// </summary>
        public ulong? GpuVramUsedBytes
        {
            get => _gpuVramUsedBytes;
            set => Set(ref _gpuVramUsedBytes, value);
        }

        /// <summary>
        /// Total dedicated GPU memory, in bytes. Null when the figure is
        /// not readable.
        /// </summary>
        public ulong? GpuVramTotalBytes
        {
            get => _gpuVramTotalBytes;
            set => Set(ref _gpuVramTotalBytes, value);
        }

        /// <summary>
        /// CPU clock rate in megahertz. Null when the figure is not
        /// readable.
        /// </summary>
        public double? CpuClockMHz
        {
            get => _cpuClockMHz;
            set => Set(ref _cpuClockMHz, value);
        }

        /// <summary>
        /// Assigns a value and notifies only when it actually changed.
        /// </summary>
        private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;

            field = value;
            OnPropertyChanged(propertyName);
        }

        /// <summary>
        /// Raises the change notification.
        /// </summary>
        protected void OnPropertyChanged(string? name)
        {
            if (name is null)
                return;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
