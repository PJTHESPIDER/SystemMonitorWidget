using System;
using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using WinRT;

namespace System_Monitor.COM
{
    internal static class Guids
    {
        public const string IClassFactory = "00000001-0000-0000-C000-000000000046";
        public const string IUnknown = "00000000-0000-0000-C000-000000000046";
    }

    [ComImport]
    [ComVisible(false)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid(Guids.IClassFactory)]
    internal interface IClassFactory
    {
        [PreserveSig]
        int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

        [PreserveSig]
        int LockServer(bool fLock);
    }

    [ComVisible(true)]
    internal class WidgetProviderFactory<T> : IClassFactory where T : IWidgetProvider, new()
    {
        public int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
        {
            ppvObject = IntPtr.Zero;

            if (pUnkOuter != IntPtr.Zero)
            {
                Marshal.ThrowExceptionForHR(CLASS_E_NOAGGREGATION);
            }

            if (riid == typeof(T).GUID || riid == Guid.Parse(Guids.IUnknown))
            {
                ppvObject = MarshalInspectable<IWidgetProvider>.FromManaged(new T());
                return 0;
            }

            // Fallback via QueryInterface
            var pInspectable = MarshalInspectable<IWidgetProvider>.FromManaged(new T());
            int hr = Marshal.QueryInterface(pInspectable, ref riid, out ppvObject);
            Marshal.Release(pInspectable);
            if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(E_NOINTERFACE);
            }
            return 0;
        }

        public int LockServer(bool fLock)
        {
            return 0;
        }

        private const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
        private const int E_NOINTERFACE = unchecked((int)0x80004002);
    }

    internal static class ComServer
    {
        [DllImport("ole32.dll")]
        public static extern int CoRegisterClassObject(
            [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
            [MarshalAs(UnmanagedType.IUnknown)] object pUnk,
            uint dwClsContext,
            uint flags,
            out uint lpdwRegister);

        [DllImport("ole32.dll")]
        public static extern int CoRevokeClassObject(uint dwRegister);

        public const uint CLSCTX_LOCAL_SERVER = 0x4;
        public const uint REGCLS_MULTIPLEUSE = 0x1;
    }
}

