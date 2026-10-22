using System;
using System.Collections.Generic;
using LibreHardwareMonitor.Hardware;
using LibreHardwareMonitor.Hardware.Gpu;

namespace System_Monitor.Services
{
    /// <summary>
    /// Read-only LibreHardwareMonitor sensor source.
    ///
    /// Lifetime: a single <see cref="Computer"/> instance is created and
    /// opened once, on first use, and closed on <see cref="Dispose"/>. The
    /// instance is never reopened and initialization is never retried in a
    /// tight loop — if initialization fails, the source stays unavailable for
    /// the rest of its life and the widget continues to operate using the
    /// vendor-neutral Windows collectors.
    ///
    /// Read-only: this class only ever reads <see cref="ISensor.Value"/>.
    /// It never writes an <see cref="IControl"/> value, never changes a
    /// hardware setting, and never requires elevation.
    ///
    /// Hardware support in LibreHardwareMonitorLib 0.9.6:
    ///   - CPU: Intel and AMD (MSR-based temperature/clock/load). Reading
    ///     these typically requires elevated privileges; without them the
    ///     sensors exist but report no value.
    ///   - GPU: NVIDIA (NVAPI), AMD (ADL) and Intel (GCL / D3D).
    /// </summary>
    public sealed class LibreHardwareMonitorSource : ILhmSensorSource
    {
        /// <summary>
        /// The backend name used in the diagnostic log.
        /// </summary>
        public const string BackendName = "LHM";

        private readonly object _lock = new();
        private readonly TimeProvider _timeProvider;
        private Computer? _computer;
        private bool _initialized;
        private bool _disposed;

        public bool IsAvailable { get; private set; }

        public string? UnavailableReason { get; private set; }

        public LibreHardwareMonitorSource()
            : this(null)
        {
        }

        /// <summary>
        /// Creates the source with an explicit clock, so the elapsed times
        /// in the log can be controlled in tests.
        /// </summary>
        public LibreHardwareMonitorSource(TimeProvider? timeProvider)
        {
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>
        /// Reads the current devices, refreshing sensors first.
        /// Never throws: an unavailable or failing backend yields an empty
        /// device list so the widget keeps operating.
        /// </summary>
        public IReadOnlyList<LhmDeviceDescriptor> Read()
        {
            var devices = new List<LhmDeviceDescriptor>();

            lock (_lock)
            {
                if (_disposed)
                    return devices;

                if (!EnsureInitialized())
                    return devices;

                var started = _timeProvider.GetUtcNow();

                try
                {
                    foreach (var hardware in _computer!.Hardware)
                    {
                        UpdateRecursive(hardware);

                        var descriptor = Describe(hardware);
                        if (descriptor is not null)
                            devices.Add(descriptor);
                    }

                    DiagnosticLog.Shared.Info(
                        BackendName,
                        "read",
                        "ok",
                        elapsed: _timeProvider.GetUtcNow() - started,
                        detail: $"devices={devices.Count}");
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Shared.Error(
                        BackendName,
                        "read",
                        "failed",
                        elapsed: _timeProvider.GetUtcNow() - started,
                        detail: DiagnosticDetail.Describe(ex));
                }
            }

            return devices;
        }

        /// <summary>
        /// Opens the Computer instance exactly once. Failures are recorded and
        /// are not retried, so a blocked driver does not cause repeated
        /// expensive initialization attempts on every poll.
        /// </summary>
        private bool EnsureInitialized()
        {
            if (_initialized)
                return IsAvailable;

            _initialized = true;

            var started = _timeProvider.GetUtcNow();

            try
            {
                var computer = new Computer
                {
                    // Only CPU and GPU are needed. Everything else is disabled
                    // so the library does not spend time enumerating storage,
                    // network, controller and power-monitor devices that the
                    // widget does not read.
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMemoryEnabled = false,
                    IsMotherboardEnabled = false,
                    IsStorageEnabled = false,
                    IsNetworkEnabled = false,
                    IsControllerEnabled = false,
                    IsPsuEnabled = false,
                    IsBatteryEnabled = false,
                    IsPowerMonitorEnabled = false,
                };

                computer.Open();
                _computer = computer;
                IsAvailable = true;
                UnavailableReason = null;

                DiagnosticLog.Shared.Info(
                    BackendName,
                    "initialize",
                    "ok",
                    elapsed: _timeProvider.GetUtcNow() - started);
            }
            catch (Exception ex)
            {
                IsAvailable = false;
                UnavailableReason = ex.Message;

                DiagnosticLog.Shared.Error(
                    BackendName,
                    "initialize",
                    "failed",
                    elapsed: _timeProvider.GetUtcNow() - started,
                    detail: DiagnosticDetail.Describe(ex));
            }

            return IsAvailable;
        }

        /// <summary>
        /// Updates a hardware node and all of its sub-hardware, so sensors
        /// held by sub-hardware (for example a GPU's memory controller) are
        /// refreshed too.
        /// </summary>
        private static void UpdateRecursive(IHardware hardware)
        {
            try
            {
                hardware.Update();
            }
            catch (Exception ex)
            {
                // The device's name is not logged: a routine log describes
                // the backend's behaviour, not the machine's inventory. The
                // hardware type is enough to tell a CPU from a GPU.
                DiagnosticLog.Shared.Warning(
                    BackendName,
                    "update",
                    "failed",
                    detail: $"type={hardware.HardwareType} {DiagnosticDetail.Describe(ex)}");
            }

            foreach (var sub in hardware.SubHardware)
                UpdateRecursive(sub);
        }

        private static LhmDeviceDescriptor? Describe(IHardware hardware)
        {
            var hardwareType = hardware.HardwareType;

            var isCpu = hardwareType == HardwareType.Cpu;
            var isGpu = hardwareType is HardwareType.GpuNvidia
                or HardwareType.GpuAmd
                or HardwareType.GpuIntel;

            if (!isCpu && !isGpu)
                return null;

            // The backend's own identifier is the durable identity. It is
            // stable across collections and encodes vendor and instance, and
            // it is never the device's position in a list.
            var identifier = hardware.Identifier?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(identifier))
                return null;

            var sensors = CollectSensors(hardware);

            return new LhmDeviceDescriptor
            {
                DeviceId = identifier,
                DisplayName = hardware.Name ?? string.Empty,
                Vendor = DeriveVendor(hardwareType, identifier),
                IsCpu = isCpu,
                IsIntegratedGpu = isGpu ? DeriveIntegration(hardwareType, identifier) : null,
                PciDeviceKey = isGpu ? ReadPciDeviceKey(hardware) : null,
                InstanceId = isGpu ? ReadInstanceId(hardware) : null,
                Sensors = sensors,
            };
        }

        /// <summary>
        /// Reads the device's per-instance identity — the identifier the
        /// platform assigns to one specific adapter.
        ///
        /// <para>
        /// For GPUs the library exposes it as <see cref="GenericGpu.DeviceId"/>:
        /// the D3D adapter's actual device identifier (NVIDIA and Intel), or
        /// the adapter's PNP string (AMD). It identifies the individual card,
        /// so two identical cards have different values, and it does not
        /// change when the device set is re-enumerated.
        /// </para>
        ///
        /// <para>
        /// That makes it the right identity to persist a user selection
        /// against. <see cref="IHardware.Identifier"/> cannot be used for
        /// that: it is an enumeration index ("/gpu-nvidia/0") whose numeric
        /// suffix shifts whenever the device set changes.
        /// </para>
        ///
        /// <para>
        /// The value is read defensively. A backend that cannot supply one
        /// yields null, and the caller then falls back to the default
        /// selection policy rather than persisting something it cannot
        /// resolve later.
        /// </para>
        /// </summary>
        private static string? ReadInstanceId(IHardware hardware)
        {
            if (hardware is not GenericGpu gpu)
                return null;

            string? deviceId;
            try
            {
                deviceId = gpu.DeviceId;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Shared.Warning(
                    BackendName,
                    "read-instance-id",
                    "failed",
                    detail: DiagnosticDetail.Describe(ex));
                return null;
            }

            return string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();
        }

        /// <summary>
        /// Reads the device's PCI vendor and device id, in the canonical
        /// "VEN_xxxx&amp;DEV_yyyy" form.
        ///
        /// <see cref="IHardware.Identifier"/> is an enumeration index
        /// ("/gpu-nvidia/0"), which says nothing about which physical card
        /// was measured. The GPU's own device id is the Windows PNP device
        /// id ("PCI\VEN_10DE&amp;DEV_1E91&amp;..."), which does. It is what
        /// lets a reading from a vendor command-line tool be attributed to
        /// this device rather than to whichever card happened to be first.
        ///
        /// The property is on the library's GPU base type rather than on
        /// <see cref="IHardware"/>, so it is read through a down-cast. When
        /// the cast is not possible, or the id does not carry both parts, the
        /// result is null: the device simply cannot be reconciled, which is
        /// preferable to reconciling it wrongly.
        /// </summary>
        private static string? ReadPciDeviceKey(IHardware hardware)
        {
            if (hardware is not GenericGpu gpu)
                return null;

            string? deviceId;
            try
            {
                deviceId = gpu.DeviceId;
            }
            catch (Exception ex)
            {
                DiagnosticLog.Shared.Warning(
                    BackendName,
                    "read-pci-device-id",
                    "failed",
                    detail: DiagnosticDetail.Describe(ex));
                return null;
            }

            var key = NvidiaGpuIdentity.ExtractPciDeviceKey(deviceId);
            return string.IsNullOrEmpty(key) ? null : key;
        }

        /// <summary>
        /// Collects sensors from a hardware node and its sub-hardware.
        /// Sensor values that are null, NaN or infinite are recorded as null
        /// (unavailable) rather than zero.
        /// </summary>
        private static List<LhmSensorDescriptor> CollectSensors(IHardware hardware)
        {
            var result = new List<LhmSensorDescriptor>();
            CollectSensors(hardware, result);
            return result;
        }

        private static void CollectSensors(IHardware hardware, List<LhmSensorDescriptor> into)
        {
            foreach (var sensor in hardware.Sensors)
            {
                into.Add(new LhmSensorDescriptor
                {
                    Name = sensor.Name ?? string.Empty,
                    SensorType = sensor.SensorType.ToString(),
                    Value = Normalize(sensor.Value),
                    Unit = DeriveUnit(sensor.SensorType),
                });
            }

            foreach (var sub in hardware.SubHardware)
                CollectSensors(sub, into);
        }

        private static double? Normalize(float? value)
        {
            if (value is not { } v)
                return null;
            if (float.IsNaN(v) || float.IsInfinity(v))
                return null;
            return v;
        }

        /// <summary>
        /// Maps a sensor type onto the unit its value is expressed in.
        ///
        /// LibreHardwareMonitor reports temperatures in degrees Celsius and
        /// clock speeds in megahertz. Memory sensors (SmallData) report
        /// megabytes, which is verified before any conversion to bytes.
        /// </summary>
        private static LhmSensorUnit DeriveUnit(SensorType sensorType) => sensorType switch
        {
            SensorType.Temperature => LhmSensorUnit.Celsius,
            SensorType.Clock => LhmSensorUnit.Megahertz,
            SensorType.Load => LhmSensorUnit.Percent,
            SensorType.SmallData => LhmSensorUnit.Megabyte,
            _ => LhmSensorUnit.Unknown,
        };

        /// <summary>
        /// Derives the vendor from the hardware type, which is authoritative
        /// in this library version. The hardware's Properties dictionary is
        /// empty in 0.9.6, so it is not used for identity.
        /// </summary>
        private static string? DeriveVendor(HardwareType hardwareType, string identifier) => hardwareType switch
        {
            HardwareType.Cpu => VendorFromCpuIdentifier(identifier),
            HardwareType.GpuNvidia => "NVIDIA",
            HardwareType.GpuAmd => "AMD",
            HardwareType.GpuIntel => "Intel",
            _ => null,
        };

        /// <summary>
        /// CPU identifiers in this library version are "/intelcpu/0",
        /// "/amdcpu/0" or "/genericcpu/0".
        /// </summary>
        private static string? VendorFromCpuIdentifier(string identifier)
        {
            if (identifier.StartsWith("/intelcpu/", StringComparison.OrdinalIgnoreCase))
                return "Intel";
            if (identifier.StartsWith("/amdcpu/", StringComparison.OrdinalIgnoreCase))
                return "AMD";
            return null;
        }

        /// <summary>
        /// Derives whether a GPU is integrated with the CPU.
        ///
        /// This library version uses distinct identifier prefixes for Intel's
        /// integrated ("/gpu-intel-integrated/") and discrete ("/gpu-intel/")
        /// GPUs. For NVIDIA and AMD the integration cannot be reliably
        /// determined from the identifier, so the result is null (Unknown)
        /// rather than a guess.
        /// </summary>
        private static bool? DeriveIntegration(HardwareType hardwareType, string identifier)
        {
            if (hardwareType == HardwareType.GpuIntel)
            {
                if (identifier.StartsWith("/gpu-intel-integrated/", StringComparison.OrdinalIgnoreCase))
                    return true;
                if (identifier.StartsWith("/gpu-intel/", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            return null;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;
                _disposed = true;

                try
                {
                    _computer?.Close();
                }
                catch (Exception ex)
                {
                    DiagnosticLog.Shared.Warning(
                        BackendName,
                        "close",
                        "failed",
                        detail: DiagnosticDetail.Describe(ex));
                }
                finally
                {
                    _computer = null;
                }
            }
        }
    }
}
