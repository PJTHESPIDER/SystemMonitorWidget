using System;
using System.Runtime.InteropServices;
using System_Monitor.COM;
using System_Monitor.Models;
using System_Monitor.Services;

namespace System_Monitor
{
    public static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // The routine log is off unless the user has opted in, so this
            // writes nothing on a normal run. The opt-in inventory export
            // is honoured whether or not the log is enabled.
            DiagnosticHost.Initialize();

            if (DiagnosticHost.TryGetExportRequest(args, out var exportPath))
            {
                WriteRequestedInventory(args, exportPath!);
            }

            WinRT.ComWrappersSupport.InitializeComWrappers();

            // Running as out-of-process COM Server for the Windows 11 Widget Board
            uint cookie = 0;
            int hr = ComServer.CoRegisterClassObject(
                SystemMonitorWidgetProvider.Clsid,
                new WidgetProviderFactory<SystemMonitorWidgetProvider>(),
                ComServer.CLSCTX_LOCAL_SERVER,
                ComServer.REGCLS_MULTIPLEUSE,
                out cookie);

            if (hr < 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }

            // Wait until all widgets are deactivated/deleted or host disconnects
            SystemMonitorWidgetProvider.WaitForCompletion();

            // Stop the update pipeline and await outstanding work before
            // disposing owned resources — including the sensor backend — so
            // the backend is never disposed while the pipeline may still be
            // using it.
            SystemMonitorWidgetProvider.ShutdownAsync().GetAwaiter().GetResult();

            if (cookie != 0)
            {
                ComServer.CoRevokeClassObject(cookie);
            }
        }

        /// <summary>
        /// Writes the hardware inventory the user asked for on the command
        /// line.
        ///
        /// <para>
        /// A collection is performed here, so the export describes the
        /// machine as it actually is rather than an empty shell. The
        /// collection is read-only: it reads sensors and counters and
        /// writes nothing to the hardware.
        /// </para>
        ///
        /// <para>
        /// The export runs before the widget host is contacted, so it works
        /// even when the provider cannot start — which is exactly when a
        /// user is most likely to want it.
        /// </para>
        /// </summary>
        private static void WriteRequestedInventory(string[] args, string path)
        {
            DiagnosticLog.Shared.Info(
                "diagnostics",
                "inventory-request",
                "ok",
                detail: "export requested on the command line");

            MetricsSnapshot? snapshot = null;

            try
            {
                using var service = new SystemMetricsService(new SystemMetrics());
                snapshot = service.Collect();
            }
            catch (Exception ex)
            {
                // A failed collection still produces an export: the file
                // says what was collected and what was not.
                DiagnosticLog.Shared.Warning(
                    "diagnostics",
                    "inventory-collect",
                    "failed",
                    detail: DiagnosticDetail.Describe(ex));
            }

            DiagnosticHost.ExportIfRequested(args, snapshot);
        }
    }
}

