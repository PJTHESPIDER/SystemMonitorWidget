using System;
using System.Linq;
using System.Runtime.InteropServices;
using System_Monitor.COM;
using System_Monitor.Services;

namespace System_Monitor
{
    public static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();

            bool isComServer = args.Any(arg =>
                arg.Contains("-RegisterProcessAsComServer", StringComparison.OrdinalIgnoreCase) ||
                arg.Contains("/RegisterProcessAsComServer", StringComparison.OrdinalIgnoreCase));

            if (isComServer)
            {
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

                if (cookie != 0)
                {
                    ComServer.CoRevokeClassObject(cookie);
                }
            }
            else
            {
                // Started directly by user (desktop / debug view)
                uint cookie = 0;
                try
                {
                    ComServer.CoRegisterClassObject(
                        SystemMonitorWidgetProvider.Clsid,
                        new WidgetProviderFactory<SystemMonitorWidgetProvider>(),
                        ComServer.CLSCTX_LOCAL_SERVER,
                        ComServer.REGCLS_MULTIPLEUSE,
                        out cookie);
                }
                catch { }

                global::Microsoft.UI.Xaml.Application.Start((p) =>
                {
                    var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                        global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                    global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                });

                if (cookie != 0)
                {
                    try { ComServer.CoRevokeClassObject(cookie); } catch { }
                }
            }
        }
    }
}

