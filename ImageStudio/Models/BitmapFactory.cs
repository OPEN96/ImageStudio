using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageStudio.Models;

/// <summary>像素数据 → Avalonia 位图（直通 alpha 预乘）。</summary>
public static class BitmapFactory
{
    public static WriteableBitmap ToBitmap(Image<Bgra32> image)
    {
        var width = image.Width;
        var height = image.Height;

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        var row = new byte[width * 4];

        using var fb = bitmap.Lock();
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var span = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var p = span[x];
                    var a = p.A;
                    var i = x * 4;
                    row[i] = (byte)((p.B * a + 127) / 255);
                    row[i + 1] = (byte)((p.G * a + 127) / 255);
                    row[i + 2] = (byte)((p.R * a + 127) / 255);
                    row[i + 3] = a;
                }

                Marshal.Copy(row, 0, fb.Address + y * fb.RowBytes, width * 4);
            }
        });

        return bitmap;
    }
}
