using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TimeGuard.Branding;

internal static class ScreenTimeBranding
{
    internal static ImageSource Image => (ImageSource)System.Windows.Application.Current.FindResource("ScreenTimeBrandImage");

    // Render the same replaceable vector into an owned ICO; no duplicate tray artwork or HICON leak.
    internal static System.Drawing.Icon CreateTrayIcon(ImageSource image)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) drawing.DrawImage(image, new Rect(0, 0, 32, 32));
        var bitmap = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);
        using var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, System.Text.Encoding.UTF8, true))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
            writer.Write((byte)32); writer.Write((byte)32); writer.Write((byte)0); writer.Write((byte)0);
            writer.Write((ushort)1); writer.Write((ushort)32); writer.Write((uint)png.Length); writer.Write((uint)22);
            writer.Write(png.ToArray());
        }
        ico.Position = 0;
        using var icon = new System.Drawing.Icon(ico);
        return (System.Drawing.Icon)icon.Clone();
    }
}
