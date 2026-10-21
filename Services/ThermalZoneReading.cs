using System;

namespace System_Monitor.Services
{
    /// <summary>
    /// Source of a thermal-zone temperature reading, used to avoid labeling
    /// unidentified ACPI sensors as CPU- or GPU-specific.
    /// </summary>
    public enum ThermalZoneSource
    {
        /// <summary>ACPI MSAcpi_ThermalZoneTemperature; component identity unknown.</summary>
        AcpiUnidentified,

        /// <summary>NVIDIA GPU via nvidia-smi temperature.gpu.</summary>
        NvidiaSmiGpu,
    }

    /// <summary>
    /// A single thermal-zone temperature reading with an explicit source.
    /// </summary>
    public sealed record ThermalZoneReading(string InstanceName, double TemperatureC, ThermalZoneSource Source);
}
