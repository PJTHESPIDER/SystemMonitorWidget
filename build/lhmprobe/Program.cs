using System;
using System.Collections.Generic;
using System.Linq;
using LibreHardwareMonitor.Hardware;

// Probes the retention semantics of LibreHardwareMonitorLib 0.9.6:
// when a hardware node's Update() fails, are the sensor values left
// holding their previous values?
class Probe
{
    static void Main()
    {
        var computer = new Computer
        {
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

        var before = new Dictionary<string, float?>();

        foreach (var hw in computer.Hardware)
        {
            Console.WriteLine($"HW type={hw.HardwareType} name={hw.Name} ident={hw.Identifier}");
            try { hw.Update(); }
            catch (Exception ex) { Console.WriteLine($"  !! Update threw {ex.GetType().Name}"); }

            foreach (var s in hw.Sensors)
            {
                before[Key(hw, s.Name, s.SensorType)] = s.Value;
            }
            Dump(hw, "  ");
        }

        // Now force every hardware node's Update() to fail, by closing the
        // Computer underneath it. The sensors should still hold the values
        // they last read: that is the "old sensor values remain accessible"
        // case.
        Console.WriteLine();
        Console.WriteLine("=== forcing update failure (Close() then Update()) ===");
        computer.Close();

        foreach (var hw in computer.Hardware)
        {
            try
            {
                hw.Update();
                Console.WriteLine($"HW {hw.Identifier} Update did NOT throw");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"HW {hw.Identifier} Update threw {ex.GetType().Name}");
            }

            foreach (var s in hw.Sensors)
            {
                var k = Key(hw, s.Name, s.SensorType);
                var was = before.TryGetValue(k, out var b) ? b : (float?)null;
                var retained = was.HasValue && s.Value.HasValue && Math.Abs(was.Value - s.Value.Value) < 1e-6;
                if (was.HasValue || s.Value.HasValue)
                {
                    Console.WriteLine(
                        $"  SENSOR name='{s.Name}' type={s.SensorType} before={(was.HasValue ? was.Value.ToString("F3") : "(null)")} " +
                        $"after={(s.Value.HasValue ? s.Value.Value.ToString("F3") : "(null)")} " +
                        $"retained={(retained ? "YES" : "no")}");
                }
            }
        }
    }

    static string Key(IHardware hw, string name, LibreHardwareMonitor.Hardware.SensorType type) =>
        $"{hw.Identifier}|{name}|{type}";

    static void Dump(IHardware hw, string indent)
    {
        foreach (var s in hw.Sensors)
        {
            Console.WriteLine($"{indent}SENSOR name='{s.Name}' type={s.SensorType} value={(s.Value.HasValue ? s.Value.Value.ToString("F3") : "(null)")}");
        }
        foreach (var sub in hw.SubHardware)
        {
            Console.WriteLine($"{indent}SUBHW type={sub.HardwareType} name={sub.Name}");
            Dump(sub, indent + "  ");
        }
    }
}
