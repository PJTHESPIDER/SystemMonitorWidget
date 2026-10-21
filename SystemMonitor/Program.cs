using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
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
    }
}

