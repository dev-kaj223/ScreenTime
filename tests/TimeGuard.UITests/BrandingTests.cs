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
            // The interim clock's closing arc extends 0.005 DIP left of zero.
            Assert.InRange(image.Drawing.Bounds.X, -0.01, 0.01);
            Assert.InRange(image.Drawing.Bounds.Y, -0.01, 0.01);
            Assert.InRange(image.Drawing.Bounds.Width, 31.99, 32.01);
            Assert.InRange(image.Drawing.Bounds.Height, 31.99, 32.01);
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
        window.CaptureToFile(Path.Combine(AppContext.BaseDirectory, "phase8-first-run.png"));
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
}
