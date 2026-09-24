using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TimeGuard.Helpers;

/// <summary>Only configures our own HWND. No input, game, or global hooks.</summary>
internal static class NonActivatingWindowHelper
{
    internal const int PassiveStyles = 0x08000000 | 0x80 | 0x20 | 0x80000; // NOACTIVATE, TOOLWINDOW, TRANSPARENT, LAYERED
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetActiveWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetFocus();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    internal static IntPtr Configure(Window window, IntPtr foreground)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLong(hwnd, -20, GetWindowLong(hwnd, -20) | PassiveStyles);
        if (previous == 0 && Marshal.GetLastPInvokeError() != 0) throw new System.ComponentModel.Win32Exception();
        HwndSource.FromHwnd(hwnd).AddHook(WindowMessage);
        // Monitor work bounds are physical pixels; WPF dimensions are DIPs.
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(foreground, 2), ref info)) throw new System.ComponentModel.Win32Exception();
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(window);
        var width = Math.Min((int)Math.Ceiling(window.Width * dpi.DpiScaleX), info.Work.Right - info.Work.Left);
        var height = Math.Min((int)Math.Ceiling(window.Height * dpi.DpiScaleY), info.Work.Bottom - info.Work.Top);
        if (!SetWindowPos(hwnd, new IntPtr(-1), Math.Max(info.Work.Left, info.Work.Right - width - 20),
            Math.Max(info.Work.Top, info.Work.Bottom - height - 20), width, height, 0x10 | 0x20)) // NOACTIVATE, FRAMECHANGED
            throw new System.ComponentModel.Win32Exception();
        return hwnd;
    }

    private static IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == 0x21) { handled = true; return new IntPtr(3); } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        if (message == 0x84) { handled = true; return new IntPtr(-1); } // defensive HTTRANSPARENT; layered style supplies cross-thread pass-through
        return IntPtr.Zero;
    }
}
