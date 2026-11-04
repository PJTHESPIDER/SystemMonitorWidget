using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using LibreHardwareMonitor.Hardware;

/// <summary>
/// Refined CPU Temperature Probe - corrected PawnIo type lookup and diagnostics
/// </summary>
class CpuTempProbe
{
    static int Main(string[] args)
    {
        var process = Process.GetCurrentProcess();
        var isElevated = IsProcessElevated();
        var asm = Assembly.GetExecutingAssembly();
        var lhmAsm = typeof(Computer).Assembly;

        Console.WriteLine($"=== CPU Temperature Probe ===");
        Console.WriteLine($"Timestamp: {DateTimeOffset.Now:o}");
        Console.WriteLine($"Process ID: {process.Id}");
        Console.WriteLine($"Elevated: {isElevated}");
        Console.WriteLine($"Probe Assembly: {asm.GetName().Name} v{asm.GetName().Version}");
        Console.WriteLine($"LHM Assembly: {lhmAsm.GetName().Name} v{lhmAsm.GetName().Version}");
        Console.WriteLine($"LHM Location: {lhmAsm.Location}");
        Console.WriteLine();

        // List all types containing "Pawn" if requested
        if (args.Length > 0 && args[0] == "--list-pawn-types")
        {
            ListPawnTypes(lhmAsm);
            return 0;
        }

        var computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = false,
            IsMemoryEnabled = false,
            IsMotherboardEnabled = false,
            IsStorageEnabled = false,
            IsNetworkEnabled = false,
            IsControllerEnabled = false,
            IsPsuEnabled = false,
            IsBatteryEnabled = false,
            IsPowerMonitorEnabled = false,
        };

        try
        {
            Console.WriteLine("--- Opening Computer ---");
            computer.Open();
            Console.WriteLine("Computer opened successfully");
            Console.WriteLine();

            // Print PawnIo driver status with correct type name
            PrintPawnIoStatus(lhmAsm);

            // Also check for any LHM diagnostic/report API
            CheckLhmDiagnostics(computer);

            // Recursively enumerate all CPU hardware and subhardware
            Console.WriteLine("--- Hardware Enumeration ---");
            var cpuHardwareList = new List<IHardware>();
            foreach (var hw in computer.Hardware)
            {
                if (hw.HardwareType == HardwareType.Cpu)
                {
                    cpuHardwareList.Add(hw);
                    EnumerateHardwareRecursive(hw, 0);
                }
            }
            Console.WriteLine();

            // App's CPU temperature selection policy (from LhmSensorSelection.cs)
            Console.WriteLine("--- App Selection Policy (CPU Temperature) ---");
            Console.WriteLine("Candidate preference order:");
            Console.WriteLine("  1. CPU Package");
            Console.WriteLine("  2. Core Tctl/Tdie");
            Console.WriteLine("  3. Core Tdie");
            Console.WriteLine("  4. Core Average");
            Console.WriteLine("  5. Core Max");
            Console.WriteLine("  6. CCDs Average");
            Console.WriteLine("Sensor must be: Type=Temperature, readable (Value.HasValue)");
            Console.WriteLine();

            // Take several timestamped samples
            const int sampleCount = 3;
            const int sampleIntervalMs = 1000;

            for (int sample = 1; sample <= sampleCount; sample++)
            {
                var sampleTime = DateTimeOffset.Now;
                Console.WriteLine($"=== Sample {sample}/{sampleCount} at {sampleTime:o} ===");

                // Update all CPU hardware recursively, recording exceptions
                var updateExceptions = new List<(string hardwareId, string error)>();
                foreach (var cpuHw in cpuHardwareList)
                {
                    UpdateRecursiveWithExceptions(cpuHw, updateExceptions);
                }

                // Report any update exceptions
                if (updateExceptions.Count > 0)
                {
                    Console.WriteLine("  UPDATE EXCEPTIONS:");
                    foreach (var (id, err) in updateExceptions)
                    {
                        Console.WriteLine($"    [{id}] {err}");
                    }
                }
                else
                {
                    Console.WriteLine("  UPDATE: All hardware updated successfully (no exceptions)");
                }

                // Log all sensors with raw values
                Console.WriteLine("  SENSORS:");
                foreach (var cpuHw in cpuHardwareList)
                {
                    DumpSensorsRecursive(cpuHw, "    ");
                }

                // Apply app's selection policy to current readings
                Console.WriteLine("  SELECTION POLICY RESULT:");
                var selected = ApplySelectionPolicy(cpuHardwareList);
                if (selected.HasValue)
                {
                    Console.WriteLine($"    SELECTED: {selected.Value.sensorName} = {selected.Value.value:F3}°C (source: {selected.Value.source})");
                }
                else
                {
                    Console.WriteLine("    SELECTED: NONE (all candidates unavailable)");
                }

                Console.WriteLine();

                if (sample < sampleCount)
                {
                    System.Threading.Thread.Sleep(sampleIntervalMs);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FATAL: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return 1;
        }
        finally
        {
            Console.WriteLine("--- Closing Computer ---");
            try
            {
                computer.Close();
                Console.WriteLine("Computer closed successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Close() threw: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return 0;
    }

    static void ListPawnTypes(Assembly lhmAsm)
    {
        Console.WriteLine("--- Types containing 'Pawn' (case-insensitive) ---");
        var pawnTypes = lhmAsm.GetTypes()
            .Where(t => t.FullName != null && t.FullName.Contains("Pawn", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (pawnTypes.Count == 0)
        {
            Console.WriteLine("  (none found)");
        }
        else
        {
            foreach (var t in pawnTypes)
            {
                Console.WriteLine($"  {t.FullName}");
                // Print public static properties
                foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Static))
                {
                    try
                    {
                        var val = prop.GetValue(null);
                        Console.WriteLine($"    {prop.Name} = {val}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"    {prop.Name} = <error: {ex.Message}>");
                    }
                }
                // Print public instance properties (for context)
                foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    Console.WriteLine($"    {prop.Name} (instance)");
                }
            }
        }
        Console.WriteLine();
    }

    static void PrintPawnIoStatus(Assembly lhmAsm)
    {
        Console.WriteLine("--- PawnIo Status Check ---");

        // Try the correct type name per user correction
        var pawnIoType = lhmAsm.GetType("LibreHardwareMonitor.PawnIo.PawnIo");

        if (pawnIoType == null)
        {
            // Fallback: search for any type with "PawnIo" in name
            pawnIoType = lhmAsm.GetTypes()
                .FirstOrDefault(t => t.FullName != null && t.FullName.Contains("PawnIo", StringComparison.OrdinalIgnoreCase));
        }

        if (pawnIoType == null)
        {
            Console.WriteLine("PawnIo type: NOT FOUND in assembly");
            Console.WriteLine("  Status: Diagnostic API unavailable");
            Console.WriteLine();
            return;
        }

        Console.WriteLine($"PawnIo type: FOUND -> {pawnIoType.FullName}");

        // Check static properties: IsInstalled, Version
        foreach (var propName in new[] { "IsInstalled", "Version" })
        {
            var prop = pawnIoType.GetProperty(propName, BindingFlags.Public | BindingFlags.Static);
            if (prop != null)
            {
                try
                {
                    var val = prop.GetValue(null);
                    Console.WriteLine($"  {propName} (static) = {val}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {propName} (static) = <error: {ex.Message}>");
                }
            }
            else
            {
                Console.WriteLine($"  {propName} (static) = NOT FOUND");
            }
        }

        // Check instance property IsLoaded (not static - describes individual module handle)
        var isLoadedProp = pawnIoType.GetProperty("IsLoaded", BindingFlags.Public | BindingFlags.Instance);
        if (isLoadedProp != null)
        {
            Console.WriteLine($"  IsLoaded (instance property) = EXISTS (requires instance)");
        }
        else
        {
            Console.WriteLine($"  IsLoaded (instance property) = NOT FOUND");
        }

        // Try to create an instance and check IsLoaded if it has a parameterless constructor
        var ctor = pawnIoType.GetConstructor(Type.EmptyTypes);
        if (ctor != null)
        {
            try
            {
                var instance = ctor.Invoke(null);
                var isLoadedVal = isLoadedProp?.GetValue(instance);
                Console.WriteLine($"  IsLoaded (via new instance) = {isLoadedVal}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  IsLoaded (via new instance) = <error: {ex.Message}>");
            }
        }
        else
        {
            Console.WriteLine($"  IsLoaded = CANNOT CHECK (no parameterless constructor)");
        }

        Console.WriteLine();
    }

    static void CheckLhmDiagnostics(Computer computer)
    {
        Console.WriteLine("--- LHM Computer Diagnostics ---");
        try
        {
            // Check if Computer has any diagnostic/report method
            var computerType = computer.GetType();
            var reportMethod = computerType.GetMethod("GetReport", BindingFlags.Public | BindingFlags.Instance);
            if (reportMethod != null)
            {
                var report = reportMethod.Invoke(computer, null);
                Console.WriteLine($"GetReport() returned: {report?.GetType().Name ?? "null"}");
                if (report != null)
                {
                    Console.WriteLine(report.ToString());
                }
            }
            else
            {
                Console.WriteLine("GetReport() method not found on Computer");
            }

            // Check for any Properties on Computer that might contain diagnostic info
            foreach (var prop in computerType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.Name.Contains("Diag", StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Contains("Report", StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Contains("Status", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var val = prop.GetValue(computer);
                        Console.WriteLine($"  Computer.{prop.Name} = {val}");
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Diagnostics check failed: {ex.Message}");
        }
        Console.WriteLine();
    }

    static bool IsProcessElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    static void EnumerateHardwareRecursive(IHardware hw, int depth)
    {
        var indent = new string(' ', depth * 2);
        Console.WriteLine($"{indent}HW type={hw.HardwareType} name={hw.Name} ident={hw.Identifier}");

        foreach (var sub in hw.SubHardware)
        {
            EnumerateHardwareRecursive(sub, depth + 1);
        }
    }

    static void UpdateRecursiveWithExceptions(IHardware hw, List<(string, string)> exceptions)
    {
        try
        {
            hw.Update();
        }
        catch (Exception ex)
        {
            exceptions.Add((hw.Identifier.ToString(), $"{ex.GetType().Name}: {ex.Message}"));
        }

        foreach (var sub in hw.SubHardware)
        {
            UpdateRecursiveWithExceptions(sub, exceptions);
        }
    }

    static void DumpSensorsRecursive(IHardware hw, string indent)
    {
        foreach (var s in hw.Sensors)
        {
            var valStr = s.Value.HasValue ? s.Value.Value.ToString("F3") : "(null)";
            Console.WriteLine($"{indent}SENSOR id={s.Identifier} name='{s.Name}' type={s.SensorType} value={valStr}");
        }
        foreach (var sub in hw.SubHardware)
        {
            DumpSensorsRecursive(sub, indent + "  ");
        }
    }

    static (string sensorName, float value, string source)? ApplySelectionPolicy(List<IHardware> cpuHardwareList)
    {
        // Collect all temperature sensors from CPU hardware
        var tempSensors = new List<(IHardware hw, ISensor sensor)>();
        foreach (var hw in cpuHardwareList)
        {
            CollectTempSensorsRecursive(hw, tempSensors);
        }

        // Candidate preference order matching LhmSensorSelection.cs
        var preferences = new[]
        {
            ("CPU Package", "CPU Package"),
            ("Core Tctl/Tdie", "Tctl"),
            ("Core Tdie", "Tdie"),
            ("Core Average", "Core Average"),
            ("Core Max", "Core Max"),
            ("CCDs Average", "CCD"),
        };

        foreach (var (prefName, matchSubstring) in preferences)
        {
            var candidates = tempSensors
                .Where(t => t.sensor.SensorType == SensorType.Temperature
                         && t.sensor.Value.HasValue
                         && t.sensor.Name.Contains(matchSubstring, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count > 0)
            {
                var selected = candidates.FirstOrDefault(c => c.sensor.Name.Equals(prefName, StringComparison.OrdinalIgnoreCase));
                if (selected == default)
                {
                    selected = candidates.First();
                }

                return (selected.sensor.Name, selected.sensor.Value.Value, prefName);
            }
        }

        return null;
    }

    static void CollectTempSensorsRecursive(IHardware hw, List<(IHardware, ISensor)> list)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType == SensorType.Temperature)
            {
                list.Add((hw, s));
            }
        }
        foreach (var sub in hw.SubHardware)
        {
            CollectTempSensorsRecursive(sub, list);
        }
    }
}