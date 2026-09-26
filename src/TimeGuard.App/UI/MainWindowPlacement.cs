using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TimeGuard.UI;

internal static class MainWindowPlacement
{
    internal static void Center(Window window, System.Drawing.Point cursor)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var monitor = NativeMonitorFromPoint(new Point(cursor.X, cursor.Y), 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !NativeGetMonitorInfo(monitor, ref info) || !NativeGetWindowRect(hwnd, out var bounds)) return;
        var width = Math.Min(bounds.Right - bounds.Left, info.Work.Right - info.Work.Left);
        var height = Math.Min(bounds.Bottom - bounds.Top, info.Work.Bottom - info.Work.Top);
        var x = info.Work.Left + (info.Work.Right - info.Work.Left - width) / 2;
        var y = info.Work.Top + (info.Work.Bottom - info.Work.Top - height) / 2;
        // Native pixels on the invoking monitor avoid WPF virtual-desktop DPI drift.
        NativeSetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, 0x4 | 0x10);
    }

    [StructLayout(LayoutKind.Sequential)] private struct Point(int x, int y) { public int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", EntryPoint = "MonitorFromPoint")] private static extern IntPtr NativeMonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool NativeGetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool NativeGetWindowRect(IntPtr hwnd, out Rect bounds);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool NativeSetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
