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
    }
}

