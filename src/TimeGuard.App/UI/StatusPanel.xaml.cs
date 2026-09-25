using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TimeGuard.UI;

public partial class StatusPanel : Window
{
    private bool _placing;
    private bool _placementQueued;
    private readonly Point _anchor;
    public StatusPanel()
    {
        InitializeComponent();
        NativeGetCursorPos(out _anchor);
        ContentRendered += (_, _) => QueuePlacement();
        SizeChanged += (_, _) => { if (IsLoaded) QueuePlacement(); };
    }

    private void QueuePlacement()
    {
        if (_placementQueued) return;
        _placementQueued = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        { _placementQueued = false; if (IsVisible) PlaceNearTray(); }));
    }

    private void PlaceNearTray()
    {
        if (_placing) return;
        _placing = true;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var cursor = _anchor;
            var monitor = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
            if (!NativeGetMonitorInfo(NativeMonitorFromPoint(cursor, 2), ref monitor)) return;
            var area = monitor.Work;
            var dpi = VisualTreeHelper.GetDpi(this);
            MaxHeight = Math.Min(650, (area.Bottom - area.Top) / dpi.DpiScaleY - 16);
            MaxWidth = Math.Min(420, (area.Right - area.Left) / dpi.DpiScaleX - 16);
            AppListScroll.MaxHeight = Math.Max(80, MaxHeight - 125);
            UpdateLayout();
            // SizeToContent can finish after Loaded. Read the actual native bounds after rendering
            // and on subsequent size changes rather than projecting an early WPF DesiredSize.
            if (!NativeGetWindowRect(hwnd, out var bounds)) return;
            var width = bounds.Right - bounds.Left;
            var height = bounds.Bottom - bounds.Top;
            var x = cursor.X < area.Left + (area.Right - area.Left) / 2 ? area.Left + 8 : area.Right - width - 8;
            var y = cursor.Y < area.Top + (area.Bottom - area.Top) / 2 ? area.Top + 8 : area.Bottom - height - 8;
            NativeSetWindowPos(hwnd, IntPtr.Zero, Math.Max(area.Left, x), Math.Max(area.Top, y),
                0, 0, 0x1 | 0x10); // NOSIZE | NOACTIVATE; show above ordinary windows for this user-opened panel
        }
        finally { _placing = false; }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Point { public int X, Y; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    private static extern bool NativeGetCursorPos(out Point point);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "MonitorFromPoint")]
    private static extern IntPtr NativeMonitorFromPoint(Point point, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    private static extern bool NativeGetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    private static extern bool NativeGetWindowRect(IntPtr hwnd, out Rect bounds);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    private static extern bool NativeSetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
