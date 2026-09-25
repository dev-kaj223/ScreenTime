using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;
using TimeGuard.Branding;
using TimeGuard.Models;
using TimeGuard.Services;
using TimeGuard.UITests.Helpers;
using TimeGuard.UI;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.AutomationElements;
using NotificationKind = TimeGuard.Models.NotificationKind;
using Image = System.Windows.Controls.Image;
using Xunit;

namespace TimeGuard.UITests;

public class BrandingTests
{
    [Fact]
    public void ExecutableAndAssembly_UseScreenTimeMetadata_AndEmbeddedIcon()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "App", "ScreenTime.exe");
        Assert.True(File.Exists(executable));
        var info = FileVersionInfo.GetVersionInfo(executable);
        Assert.Equal("ScreenTime", info.ProductName);
        Assert.Equal("ScreenTime", info.FileDescription);
        Assert.Equal("ScreenTime", typeof(TimeGuard.App).Assembly.GetName().Name);
        Assert.Equal("ScreenTime", typeof(TimeGuard.App).Assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product);
        var large = new IntPtr[1]; var small = new IntPtr[1];
        try
        {
            Assert.Equal(2u, ExtractIconEx(executable, 0, large, small, 1)); // One large and one small icon.
            Assert.NotEqual(IntPtr.Zero, large[0]); Assert.NotEqual(IntPtr.Zero, small[0]);
        }
        finally { foreach (var handle in large.Concat(small)) if (handle != IntPtr.Zero) DestroyIcon(handle); }
    }

    [Fact]
    public void ExportedIcon_AllNativeSizes_MatchSharedVectorAndRuntimeTray()
    {
        TrayPresentationTests.Sta(() =>
        {
            var panel = new StatusPanel();
            var dictionary = new ResourceDictionary { Source = new Uri("/ScreenTime;component/Branding/ScreenTimeBranding.xaml", UriKind.Relative) };
            var image = (DrawingImage)dictionary["ScreenTimeBrandImage"];
            Assert.Equal(new Rect(0, 0, 32, 32), image.Drawing.Bounds);
            var mark = (GeometryDrawing)((DrawingGroup)image.Drawing).Children[1];
            Assert.Equal("#FFD7DEE9", ((SolidColorBrush)mark.Brush).Color.ToString());
            Assert.True(mark.Geometry.FillContains(new Point(16, 26))); // Approved filled base.
            Assert.True(mark.Geometry.FillContains(new Point(16, 16))); // Neck remains connected.
            Assert.False(mark.Geometry.FillContains(new Point(16, 8))); // Open upper chamber.
            var resource = System.Windows.Application.GetResourceStream(new Uri("/ScreenTime;component/Branding/ScreenTime.ico", UriKind.Relative));
            Assert.NotNull(resource);
            using var source = resource.Stream;
            using var exported = new MemoryStream(); source.CopyTo(exported);
            var runtime = ScreenTimeBranding.CreateIconBytes(image);
            int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
            Assert.Equal(sizes, FrameSizes(exported.ToArray()));
            Assert.Equal(sizes, FrameSizes(runtime));
            var diskFrames = DecodeFrames(exported.ToArray());
            var runtimeFrames = DecodeFrames(runtime);
            for (var i = 0; i < sizes.Length; i++)
            {
                Assert.Equal(Pixels(diskFrames[i]), Pixels(runtimeFrames[i]));
                var pixels = Pixels(diskFrames[i]);
                Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha == 0);
                Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 200);
                Save(diskFrames[i], $"phase8-icon-{sizes[i]}.png");
            }
            using var tray = ScreenTimeBranding.CreateTrayIcon(image);
            Assert.Equal(32, tray.Width); Assert.Equal(32, tray.Height);
            AssertExecutablePixelsMatchExport(exported.ToArray());
            panel.Close();
        });
    }

    [Fact]
    public void FirstRun_HasScreenTimeTitle_SharedNativeIcon_AndStablePasswordControls()
    {
        using var fixture = new AppFixture();
        var window = fixture.App.WaitForWindow(fixture.Automation, "Welcome to ScreenTime");
        Assert.Equal("Welcome to ScreenTime", window.Title);
        Assert.NotNull(window.FindFirstDescendant(cf => cf.ByAutomationId("PasswordBox")));
        Assert.NotNull(window.FindFirstDescendant(cf => cf.ByAutomationId("ConfirmBox")));
        Assert.Null(window.FindTextContaining("parent password"));
        var hwnd = window.Properties.NativeWindowHandle.Value;
        Assert.NotEqual(IntPtr.Zero, SendMessage(hwnd, 0x007F, new IntPtr(1), IntPtr.Zero)); // WM_GETICON / ICON_BIG
        CaptureClearWindow(window, "phase8-first-run.png");
        CaptureShellIcon(fixture, taskbar: true);
    }

    [Fact]
    public void FinalBranding_ActualTrayStatusProtectedAccessAndSettingsSurfaces()
    {
        using var fixture = new SeededAppFixture();
        CaptureShellIcon(fixture, taskbar: false);
        using (var signal = EventWaitHandle.OpenExisting(fixture.Runtime.StatusEventName)) signal.Set();
        var status = fixture.App.WaitForWindow(fixture.Automation, "ScreenTime");
        CaptureClearWindow(status, "phase8-status.png"); status.Close();
        fixture.RequestSettings();
        var prompt = fixture.App.WaitForWindow(fixture.Automation, "Protected Access");
        CaptureClearWindow(prompt, "phase8-protected-access.png");
        prompt.FindFirstDescendant(cf => cf.ByAutomationId("PasswordBox")).Click();
        Keyboard.Type(AppFixture.TestPassword); prompt.FindButton("Unlock").Invoke();
        var settings = fixture.App.WaitForWindow(fixture.Automation, "ScreenTime Settings");
        CaptureClearWindow(settings, "phase8-settings.png");
        settings.FindFirstDescendant(cf => cf.ByName("Notifications").And(cf.ByControlType(ControlType.TabItem))).AsTabItem().Select();
        CaptureClearWindow(settings, "phase8-notification-settings.png");
        settings.FindButton("Cancel").Invoke();
        Assert.False(fixture.App.HasExited);
    }

    private static void CaptureClearWindow(FlaUI.Core.AutomationElements.Window window, string name)
    {
        var hwnd = window.Properties.NativeWindowHandle.Value;
        window.SetForeground();
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0013); // Normal HWND_TOP, never topmost.
        Assert.True(SpinWait.SpinUntil(() =>
        {
            using var dpi = new PhysicalPixelScope();
            Assert.Equal(0, DwmGetWindowAttribute(hwnd, 9, out var bounds, Marshal.SizeOf<NativeRect>()));
            var r = bounds.Rectangle;
            return new[] { (0.1, 0.1), (0.9, 0.1), (0.5, 0.5), (0.1, 0.9), (0.9, 0.9) }
                .All(p => GetAncestor(WindowFromPoint(new NativePoint((int)(r.Left + p.Item1 * r.Width),
                    (int)(r.Top + p.Item2 * r.Height))), 2) == hwnd);
        }, TimeSpan.FromSeconds(3)), "Owned window must be unobscured before capturing final branding.");
        Assert.Equal(0, DwmGetWindowAttribute(hwnd, 9, out var captureBounds, Marshal.SizeOf<NativeRect>()));
        CapturePixels(captureBounds.Rectangle, name);
    }

    internal static void CaptureShellIcon(AppFixture fixture, bool taskbar, string prefix = "phase8")
    {
        using var dpi = new PhysicalPixelScope();
        var desktop = fixture.Automation.GetDesktop();
        static bool Visible(AutomationElement element) => element.Properties.IsOffscreen.TryGetValue(out var offscreen) && !offscreen;
        static string Name(AutomationElement element) => (element.Properties.Name.ValueOrDefault ?? "").Trim();
        var appId = "Appid: " + Path.Combine(AppContext.BaseDirectory, "App", "ScreenTime.exe");
        FlaUI.Core.AutomationElements.AutomationElement[] ShellRoots() => desktop.FindAllChildren()
            .Where(e => e.Properties.ClassName.ValueOrDefault is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland").ToArray();
        FlaUI.Core.AutomationElements.AutomationElement? FindIcon() => ShellRoots()
            .SelectMany(e => e.FindAllDescendants()).FirstOrDefault(e =>
                (taskbar ? e.Properties.AutomationId.ValueOrDefault == appId : Name(e).StartsWith("ScreenTime —", StringComparison.Ordinal)) && Visible(e));
        var icon = FindIcon(); FlaUI.Core.AutomationElements.Button? overflowToggle = null;
        try
        {
            if (icon is null && !taskbar)
            {
                var chevron = ShellRoots().SelectMany(e => e.FindAllDescendants()).FirstOrDefault(e =>
                    new[] { "Show hidden icons", "Hidden icon menu", "Notification Chevron" }
                        .Contains(Name(e), StringComparer.OrdinalIgnoreCase));
                Assert.NotNull(chevron);
                overflowToggle = chevron.AsButton(); overflowToggle.Invoke();
            }
            Assert.True(SpinWait.SpinUntil(() => (icon = FindIcon()) is not null, TimeSpan.FromSeconds(3)),
                "The fixture's ScreenTime shell icon must be visible for the final capture.");
            CapturePixels(icon!.BoundingRectangle, taskbar ? prefix + "-taskbar.png" : prefix + "-tray.png");
        }
        finally
        {
            // Close only the shell flyout this check opened; never leave a key held or send Escape to another app.
            if (overflowToggle is not null && ShellRoots().Any(e => Visible(e) &&
                e.ClassName is "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland")) overflowToggle.Invoke();
        }
    }

    [Fact]
    public void SharedBranding_NoticeAtScaledDpiAndLargerText_RemainsBoundedAndPassive()
    {
        TrayPresentationTests.Sta(() =>
        {
            foreach (var scale in new[] { 1.0, 1.5, 2.0 })
            {
                var request = NotificationPreviewService.Create(NotificationKind.GraceStarted,
                    NotificationPreferences.Standard, DateTimeOffset.UtcNow);
                var notice = new PassiveNoticeWindow(request, () => true, null);
                var dictionary = new ResourceDictionary { Source = new Uri("/ScreenTime;component/Branding/ScreenTimeBranding.xaml", UriKind.Relative) };
                notice.Resources.MergedDictionaries.Add(dictionary);
                var border = (Border)notice.Content;
                var content = (StackPanel)border.Child;
                var logo = (Image)((StackPanel)content.Children[0]).Children[0];
                Assert.Same(dictionary["ScreenTimeBrandImage"], logo.Source);
                foreach (var text in content.Children.OfType<TextBlock>()) text.FontSize *= 1.25;
                border.Measure(new Size(notice.Width, double.PositiveInfinity));
                Assert.InRange(border.DesiredSize.Height, notice.MinHeight, notice.MaxHeight);
                border.Arrange(new Rect(0, 0, notice.Width, border.DesiredSize.Height)); border.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(notice.Width * scale),
                    (int)Math.Ceiling(border.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(border);
                Save(bitmap, $"phase8-notice-{scale * 100:0}dpi-125text.png");
                Assert.False(notice.ShowActivated); Assert.False(notice.ShowInTaskbar);
                Assert.False(notice.Focusable); Assert.False(notice.IsHitTestVisible);
                notice.Close();
            }
        });
    }

    // UIA/native coordinates and GDI sampling must share physical pixels. FlaUI's
    // CaptureToFile rescales some surfaces under this mixed WPF/UIA test host.
    private sealed class PhysicalPixelScope : IDisposable
    {
        private readonly IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        public PhysicalPixelScope() => Assert.NotEqual(IntPtr.Zero, previous);
        public void Dispose() => SetThreadDpiAwarenessContext(previous);
    }

    private static void CapturePixels(System.Drawing.Rectangle bounds, string name)
    {
        using var dpi = new PhysicalPixelScope();
        Assert.True(bounds.Width > 0 && bounds.Height > 0);
        using var bitmap = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
        bitmap.Save(Path.Combine(AppContext.BaseDirectory, name), System.Drawing.Imaging.ImageFormat.Png);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom)
    {
        public System.Drawing.Rectangle Rectangle => System.Drawing.Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect bounds, int size);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    private static int[] FrameSizes(byte[] ico)
    {
        using var reader = new BinaryReader(new MemoryStream(ico));
        Assert.Equal(0, reader.ReadUInt16()); Assert.Equal(1, reader.ReadUInt16());
        var count = reader.ReadUInt16(); var result = new int[count];
        for (var i = 0; i < count; i++)
        {
            var size = reader.ReadByte(); result[i] = size == 0 ? 256 : size;
            var height = reader.ReadByte(); Assert.Equal(size, height);
            reader.BaseStream.Position += 14;
        }
        return result;
    }

    private static BitmapSource[] DecodeFrames(byte[] ico)
    {
        using var reader = new BinaryReader(new MemoryStream(ico));
        reader.BaseStream.Position = 4; var count = reader.ReadUInt16(); var frames = new BitmapSource[count];
        for (var i = 0; i < count; i++)
        {
            reader.BaseStream.Position = 6 + i * 16 + 8;
            var length = reader.ReadUInt32(); var offset = reader.ReadUInt32();
            using var png = new MemoryStream(ico, (int)offset, (int)length);
            frames[i] = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        }
        return frames;
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0); return pixels;
    }

    private static void AssertExecutablePixelsMatchExport(byte[] exported)
    {
        var large = new IntPtr[1]; var small = new IntPtr[1];
        try
        {
            Assert.Equal(2u, ExtractIconEx(Path.Combine(AppContext.BaseDirectory, "App", "ScreenTime.exe"), 0, large, small, 1));
            foreach (var handle in large.Concat(small))
            {
                using var extracted = (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
                using var bytes = new MemoryStream(exported);
                using var expected = new System.Drawing.Icon(bytes, extracted.Size);
                using var actualBitmap = extracted.ToBitmap(); using var expectedBitmap = expected.ToBitmap();
                Assert.Equal(expectedBitmap.Size, actualBitmap.Size);
                for (var y = 0; y < actualBitmap.Height; y++)
                for (var x = 0; x < actualBitmap.Width; x++)
                    Assert.Equal(expectedBitmap.GetPixel(x, y), actualBitmap.GetPixel(x, y));
            }
        }
        finally { foreach (var handle in large.Concat(small)) if (handle != IntPtr.Zero) DestroyIcon(handle); }
    }

    private static void Save(BitmapSource source, string name)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = File.Create(Path.Combine(AppContext.BaseDirectory, name)); encoder.Save(stream);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
