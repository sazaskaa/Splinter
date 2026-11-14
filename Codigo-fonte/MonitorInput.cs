using System;
using System.Runtime.InteropServices;

internal sealed class MonitorInput : IDisposable
{
    internal const uint DisplayPort = 0x0F;
    internal const uint Hdmi = 0x11;
    private const byte InputSource = 0x60;
    private readonly PhysicalMonitor[] monitors;

    private MonitorInput(PhysicalMonitor[] monitors)
    {
        this.monitors = monitors;
    }

    // The caller has verified that the ASUS is the primary display at (0, 0).
    internal static MonitorInput OpenPrimary()
    {
        IntPtr display = MonitorFromPoint(new Point(), 0);
        uint count;
        if (display == IntPtr.Zero || !GetNumberOfPhysicalMonitorsFromHMONITOR(display, out count) || count != 1)
            return null;

        var physical = new PhysicalMonitor[count];
        return GetPhysicalMonitorsFromHMONITOR(display, count, physical) ? new MonitorInput(physical) : null;
    }

    internal uint? Read()
    {
        uint value, maximum;
        return GetVCPFeatureAndVCPFeatureReply(monitors[0].Handle, InputSource, IntPtr.Zero, out value, out maximum)
            ? (uint?)value
            : null;
    }

    internal bool Set(uint input)
    {
        return SetVCPFeature(monitors[0].Handle, InputSource, input);
    }

    public void Dispose()
    {
        DestroyPhysicalMonitors((uint)monitors.Length, monitors);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X, Y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        internal IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        internal string Description;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("dxva2.dll")]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll")]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] devices);

    [DllImport("dxva2.dll")]
    private static extern bool DestroyPhysicalMonitors(uint count, PhysicalMonitor[] devices);

    [DllImport("dxva2.dll")]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, IntPtr type,
        out uint value, out uint maximum);

    [DllImport("dxva2.dll")]
    private static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
}
