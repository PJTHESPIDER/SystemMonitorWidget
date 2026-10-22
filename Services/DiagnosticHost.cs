using System;
using System.IO;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// Wires the diagnostic log to a destination and owns the opt-in
    /// decisions.
    ///
    /// <para><b>Opting in</b></para>
    ///
    /// <para>
    /// The routine log is off by default. A widget that runs inside the
    /// widget host has no console, so a log file is the only useful
    /// destination — and a file nobody asked for is litter. The user opts
    /// in explicitly, in one of two ways:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><description>
    /// The environment variable <c>SYSTEMMONITOR_DIAGNOSTICS</c> set to
    /// <c>1</c>, <c>true</c> or <c>on</c> turns on the routine log and
    /// writes it to the default log path.
    /// </description></item>
    /// <item><description>
    /// The command-line switch <c>--export-diagnostics &lt;path&gt;</c>
    /// writes the opt-in hardware inventory to the given path. The
    /// inventory is written on request whether or not the routine log is
    /// enabled.
    /// </description></item>
    /// </list>
    ///
    /// <para><b>Where the log goes</b></para>
    ///
    /// <para>
    /// The default log path is under the user's local application data, in
    /// a <c>SystemMonitor</c> folder. It is per-user, so no elevation is
    /// required and the widget never writes outside the user's own profile.
    /// </para>
    /// </summary>
    public static class DiagnosticHost
    {
        /// <summary>
        /// The environment variable that opts in to the routine log.
        /// </summary>
        public const string OptInEnvironmentVariable = "SYSTEMMONITOR_DIAGNOSTICS";

        /// <summary>
        /// The command-line switch that requests an inventory export.
        /// </summary>
        public const string ExportSwitch = "--export-diagnostics";

        /// <summary>
        /// The name of the log file inside the diagnostics folder.
        /// </summary>
        public const string LogFileName = "systemmonitor.log";

        /// <summary>
        /// The name of the default inventory export file.
        /// </summary>
        public const string DefaultInventoryFileName = "systemmonitor-inventory.txt";

        private static readonly object InitLock = new();
        private static bool _initialized;

        /// <summary>
        /// The log the process writes to. The same instance as
        /// <see cref="DiagnosticLog.Shared"/>, so every component's events
        /// land in one place.
        /// </summary>
        public static DiagnosticLog Log => DiagnosticLog.Shared;

        /// <summary>
        /// The default path of the routine log file.
        /// </summary>
        public static string DefaultLogPath => Path.Combine(
            DefaultDiagnosticsDirectory,
            LogFileName);

        /// <summary>
        /// The default path of the inventory export.
        /// </summary>
        public static string DefaultInventoryPath => Path.Combine(
            DefaultDiagnosticsDirectory,
            DefaultInventoryFileName);

        /// <summary>
        /// The directory the diagnostics are written to.
        /// </summary>
        public static string DefaultDiagnosticsDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SystemMonitor");

        /// <summary>
        /// True when the environment opts in to the routine log.
        /// </summary>
        public static bool IsOptedIn()
        {
            var value = Environment.GetEnvironmentVariable(OptInEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim();
            return value.Equals("1", StringComparison.Ordinal)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("on", StringComparison.OrdinalIgnoreCase)
                || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Starts the routine log when the user has opted in.
        ///
        /// <para>
        /// Calling this more than once is harmless: the sink is added only
        /// the first time, so the log does not receive every event twice.
        /// </para>
        /// </summary>
        /// <param name="log">The log to configure, or null for the shared one.</param>
        /// <param name="logPath">The log file path, or null for the default.</param>
        /// <returns>
        /// True when the routine log is now enabled, false when the user has
        /// not opted in.
        /// </returns>
        public static bool Initialize(DiagnosticLog? log = null, string? logPath = null)
        {
            var target = log ?? DiagnosticLog.Shared;

            lock (InitLock)
            {
                if (_initialized)
                    return target.Enabled;

                _initialized = true;

                if (!IsOptedIn())
                {
                    // Not opted in: nothing is recorded, and nothing is
                    // written to disk.
                    target.Enabled = false;
                    return false;
                }

                var path = logPath ?? DefaultLogPath;
                target.AddSink(new FileDiagnosticSink(path));
                target.Enabled = true;

                // Opting in means wanting the whole picture, not just the
                // failures. The successful collection events are recorded at
                // Debug level, and without this they would never reach the
                // log — leaving the user with an empty file and no way to
                // tell "nothing failed" from "nothing ran".
                target.MinimumLevel = DiagnosticLevel.Debug;

                WriteLogStartLine(target, path);

                return true;
            }
        }

        /// <summary>
        /// Records that the routine log has started.
        ///
        /// <para>
        /// Only the log file's name is recorded, never its full path: the
        /// path contains the user's account name, and a routine log has no
        /// reason to record it. The user knows where they opted in from.
        /// </para>
        /// </summary>
        /// <param name="log">The log to write to.</param>
        /// <param name="path">The full path of the log file.</param>
        public static void WriteLogStartLine(DiagnosticLog log, string path) =>
            log.Info(
                "diagnostics",
                "log-start",
                "ok",
                detail: $"routine log enabled, writing to {Path.GetFileName(path)}");

        /// <summary>
        /// Looks for the inventory-export switch in the command line.
        /// </summary>
        /// <param name="args">The command-line arguments.</param>
        /// <param name="path">The requested output path, or null when the switch was not present.</param>
        /// <returns>True when the switch was present.</returns>
        public static bool TryGetExportRequest(string[]? args, out string? path)
        {
            path = null;

            if (args is null)
                return false;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg is null)
                    continue;

                if (arg.Equals(ExportSwitch, StringComparison.OrdinalIgnoreCase))
                {
                    // The next argument is the path, when there is one.
                    path = i + 1 < args.Length ? args[i + 1] : DefaultInventoryPath;
                    return true;
                }

                // Also accept the "--export-diagnostics=path" form.
                var prefix = ExportSwitch + "=";
                if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var value = arg.Substring(prefix.Length);
                    path = string.IsNullOrWhiteSpace(value) ? DefaultInventoryPath : value;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Writes the inventory export requested on the command line.
        ///
        /// <para>
        /// This is the only path that writes the inventory, and it runs
        /// only when the user passed the switch.
        /// </para>
        /// </summary>
        /// <param name="args">The command-line arguments.</param>
        /// <param name="snapshot">The snapshot to describe, or null.</param>
        /// <param name="log">The log to report the export to, or null.</param>
        /// <returns>The path written, or null when no export was requested.</returns>
        public static string? ExportIfRequested(
            string[]? args,
            MetricsSnapshot? snapshot = null,
            DiagnosticLog? log = null)
        {
            if (!TryGetExportRequest(args, out var path) || path is null)
                return null;

            try
            {
                return DiagnosticInventory.Write(snapshot, path, log);
            }
            catch (Exception ex)
            {
                // The export is a diagnostic convenience: a failure to write
                // it must not prevent the widget from starting.
                (log ?? DiagnosticLog.Shared).Error(
                    "diagnostics",
                    "inventory-export",
                    "failed",
                    detail: ex.GetType().Name);
                return null;
            }
        }
    }
}
