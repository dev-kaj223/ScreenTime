using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TimeGuard.Branding;

internal static class ScreenTimeBranding
{
    internal static ImageSource Image => (ImageSource)System.Windows.Application.Current.FindResource("ScreenTimeBrandImage");

    // Render the shared vector at native icon sizes, including common display scaling steps.
    // The executable's checked-in ICO is generated from the same XAML by tools/Generate-BrandingIcon.ps1.
    internal static System.Drawing.Icon CreateTrayIcon(ImageSource image)
    {
        using var ico = new MemoryStream(CreateIconBytes(image));
        using var icon = new System.Drawing.Icon(ico, 32, 32);
        return (System.Drawing.Icon)icon.Clone();
    }

    internal static byte[] CreateIconBytes(ImageSource image)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var frames = new List<byte[]>();
        foreach (var size in sizes)
        {
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen()) drawing.DrawImage(image, new Rect(0, 0, size, size));
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var png = new MemoryStream();
            encoder.Save(png);
            frames.Add(png.ToArray());
        }
        using var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, true))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            var offset = 6 + 16 * sizes.Length;
            for (var i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write((uint)frames[i].Length); writer.Write((uint)offset);
                offset += frames[i].Length;
            }
            foreach (var frame in frames) writer.Write(frame);
        }
        return ico.ToArray();
    }
}
