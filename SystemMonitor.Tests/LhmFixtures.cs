using System_Monitor.Services;

namespace SystemMonitor.Tests;

/// <summary>
/// Sensor fixtures modelled on the sensor sets LibreHardwareMonitorLib 0.9.6
/// actually produces.
///
/// IMPORTANT: these are hand-written fixtures captured from the library's
/// naming scheme. They are NOT physical-hardware certification. A fixture
/// test proves the mapping logic behaves correctly for a given sensor set; it
/// does not prove that a real machine of that type produces those sensors.
/// Only running the widget on real AMD, Intel and NVIDIA hardware can do
/// that.
/// </summary>
internal static class LhmFixtures
{
    public static LhmSensorDescriptor Sensor(string name, string type, double? value, LhmSensorUnit unit) =>
        new() { Name = name, SensorType = type, Value = value, Unit = unit };

    public static LhmSensorDescriptor Temp(string name, double? value) =>
        Sensor(name, "Temperature", value, LhmSensorUnit.Celsius);

    public static LhmSensorDescriptor Load(string name, double? value) =>
        Sensor(name, "Load", value, LhmSensorUnit.Percent);

    public static LhmSensorDescriptor Clock(string name, double? value) =>
        Sensor(name, "Clock", value, LhmSensorUnit.Megahertz);

    public static LhmSensorDescriptor Memory(string name, double? value) =>
        Sensor(name, "SmallData", value, LhmSensorUnit.Megabyte);

    /// <summary>
    /// Intel CPU (identifier "/intelcpu/0"). The library exposes per-core
    /// temperatures, "Core Average"/"Core Max", "CPU Package" and
    /// per-core clocks, plus "CPU Total" load.
    /// </summary>
    public static LhmDeviceDescriptor IntelCpu(bool readable = true) => new()
    {
        DeviceId = "/intelcpu/0",
        DisplayName = "Intel Core i7-10875H",
        Vendor = "Intel",
        IsCpu = true,
        Sensors = new List<LhmSensorDescriptor>
        {
            Load("CPU Core #1 Thread #1", readable ? 12.0 : null),
            Load("CPU Core #2 Thread #1", readable ? 40.0 : null),
            Load("CPU Total", readable ? 19.44 : null),
            Load("CPU Core Max", readable ? 100.0 : null),
            Temp("Core Max", readable ? 95.0 : null),
            Temp("Core Average", readable ? 78.0 : null),
            Temp("CPU Core #1", readable ? 80.0 : null),
            Temp("CPU Core #2", readable ? 76.0 : null),
            Temp("CPU Package", readable ? 84.0 : null),
            Temp("CPU Core #1 Distance to TjMax", readable ? 20.0 : null),
            Clock("CPU Core #1", readable ? 2600.0 : null),
            Clock("CPU Core #2", readable ? 2500.0 : null),
        },
    };

    /// <summary>
    /// AMD CPU (identifier "/amdcpu/0"). The library exposes
    /// "Core (Tctl/Tdie)" and "Cores (Average)" instead of Intel's names.
    /// </summary>
    public static LhmDeviceDescriptor AmdCpu(bool readable = true) => new()
    {
        DeviceId = "/amdcpu/0",
        DisplayName = "AMD Ryzen 9 5950X",
        Vendor = "AMD",
        IsCpu = true,
        Sensors = new List<LhmSensorDescriptor>
        {
            Load("CPU Total", readable ? 33.0 : null),
            Load("CPU Core Max", readable ? 88.0 : null),
            Temp("Core (Tctl/Tdie)", readable ? 62.5 : null),
            Temp("Core (Tdie)", readable ? 61.0 : null),
            Temp("Cores (Average)", readable ? 55.0 : null),
            Clock("CPU Core #1", readable ? 4200.0 : null),
        },
    };

    /// <summary>
    /// NVIDIA discrete GPU (identifier "/gpu-nvidia/0"). The library exposes
    /// "GPU Core" as temperature, clock and load, plus "GPU Memory Used" /
    /// "GPU Memory Total" in megabytes.
    /// </summary>
    public static LhmDeviceDescriptor NvidiaGpu(bool readable = true) => new()
    {
        DeviceId = "/gpu-nvidia/0",
        DisplayName = "NVIDIA GeForce RTX 2070 Super",
        Vendor = "NVIDIA",
        IsCpu = false,
        IsIntegratedGpu = null,
        PciDeviceKey = "VEN_10DE&DEV_1E91",
        InstanceId = @"PCI\VEN_10DE&DEV_1E91&SUBSYS_14581458\4&7AADB68&0&0008",
        Sensors = new List<LhmSensorDescriptor>
        {
            Temp("GPU Core", readable ? 61.0 : null),
            Temp("GPU Hot Spot", readable ? 68.5 : null),
            Temp("GPU Memory Junction", readable ? 68.25 : null),
            Clock("GPU Core", readable ? 330.0 : null),
            Clock("GPU Memory", readable ? 405.0 : null),
            Load("GPU Core", readable ? 25.0 : null),
            Load("GPU Memory Controller", readable ? 18.0 : null),
            Load("GPU Video Engine", readable ? 0.0 : null),
            Memory("GPU Memory Used", readable ? 1233.0 : null),
            Memory("GPU Memory Free", readable ? 6958.0 : null),
            Memory("GPU Memory Total", readable ? 8192.0 : null),
            Memory("D3D Dedicated Memory Used", readable ? 1038.5 : null),
            Memory("D3D Shared Memory Used", readable ? 58.4 : null),
        },
    };

    /// <summary>
    /// AMD discrete GPU (identifier "/gpu-amd/0").
    /// </summary>
    public static LhmDeviceDescriptor AmdGpu(bool readable = true) => new()
    {
        DeviceId = "/gpu-amd/0",
        DisplayName = "AMD Radeon RX 6700 XT",
        Vendor = "AMD",
        IsCpu = false,
        IsIntegratedGpu = null,
        PciDeviceKey = "VEN_1002&DEV_73BF",
        InstanceId = @"PCI\VEN_1002&DEV_73BF&SUBSYS_164818F0\4&2A1B4C5&0&0008",
        Sensors = new List<LhmSensorDescriptor>
        {
            Temp("GPU Core", readable ? 55.0 : null),
            Temp("GPU Hot Spot", readable ? 58.0 : null),
            Temp("GPU SoC", readable ? 52.0 : null),
            Clock("GPU Core", readable ? 2400.0 : null),
            Load("GPU Core", readable ? 12.0 : null),
            Memory("GPU Memory Used", readable ? 2048.0 : null),
            Memory("GPU Memory Total", readable ? 12288.0 : null),
        },
    };

    /// <summary>
    /// Intel integrated GPU (identifier "/gpu-intel-integrated/..."). The
    /// library exposes D3D engine load sensors and shared memory, but no
    /// dedicated VRAM and no temperature.
    /// </summary>
    public static LhmDeviceDescriptor IntelIntegratedGpu(bool readable = true) => new()
    {
        DeviceId = "/gpu-intel-integrated/%5C%5C%3F%5CPCI%23VEN_8086%26DEV_9BC4",
        DisplayName = "Intel(R) UHD Graphics",
        Vendor = "Intel",
        IsCpu = false,
        IsIntegratedGpu = true,
        PciDeviceKey = "VEN_8086&DEV_9BC4",
        InstanceId = @"PCI\VEN_8086&DEV_9BC4&SUBSYS_1A2117AA\4&2E1B0C4&0&0008",
        Sensors = new List<LhmSensorDescriptor>
        {
            Load("D3D 3D", readable ? 7.0 : null),
            Load("D3D Video Decode", readable ? 0.0 : null),
            Load("D3D ", readable ? 3.0 : null),
            Memory("D3D Shared Memory Used", readable ? 15.4 : null),
            Memory("D3D Shared Memory Total", readable ? 8122.375 : null),
        },
    };

    /// <summary>
    /// A GPU with no readable sensors at all: the device is still detected,
    /// but every measurement is unavailable.
    /// </summary>
    public static LhmDeviceDescriptor GpuWithoutSensors() => new()
    {
        DeviceId = "/gpu-intel/0",
        DisplayName = "Intel(R) Arc A770",
        Vendor = "Intel",
        IsCpu = false,
        IsIntegratedGpu = false,
        PciDeviceKey = "VEN_8086&DEV_56A0",
        InstanceId = @"PCI\VEN_8086&DEV_56A0&SUBSYS_123418DA\4&1F2A3B4&0&0008",
        Sensors = new List<LhmSensorDescriptor>(),
    };

    /// <summary>
    /// A device whose sensors exist but report no value — the shape a
    /// permission-blocked or driver-blocked sensor takes.
    /// </summary>
    public static LhmDeviceDescriptor BlockedCpu() => IntelCpu(readable: false);

    /// <summary>
    /// A device where only the MSR-backed temperature and clock sensors are
    /// blocked, while the scheduler-backed load sensor still reads. This is
    /// the partial-failure case: one quantity is unavailable while the rest
    /// of the same device remains usable.
    /// </summary>
    public static LhmDeviceDescriptor PartiallyBlockedCpu() => new()
    {
        DeviceId = "/intelcpu/0",
        DisplayName = "Intel Core i7-10875H",
        Vendor = "Intel",
        IsCpu = true,
        Sensors = new List<LhmSensorDescriptor>
        {
            // Load reads: it comes from the OS scheduler, not from MSRs.
            Load("CPU Total", 19.44),
            // Temperature and clock are blocked.
            Temp("CPU Package", null),
            Temp("Core Average", null),
            Clock("CPU Core #1", null),
        },
    };

    /// <summary>
    /// A device exposing ambiguous names: the same name is used for more than
    /// one sensor type, and a value is present on the wrong-typed sensor.
    /// </summary>
    public static LhmDeviceDescriptor AmbiguousNamesGpu() => new()
    {
        DeviceId = "/gpu-nvidia/1",
        DisplayName = "Ambiguous GPU",
        Vendor = "NVIDIA",
        IsCpu = false,
        IsIntegratedGpu = null,
        Sensors = new List<LhmSensorDescriptor>
        {
            // "GPU Memory" is a Load sensor here, and "GPU Core" is a Clock.
            Load("GPU Memory", 15.06),
            Clock("GPU Core", 330.0),
            // A "Core" temperature that must win over a "GPU Hot Spot".
            Temp("GPU Hot Spot", 68.5),
            Temp("Core", 71.0),
        },
    };
}
