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

    /// <summary>
    /// 把像素写入已有的位图（尺寸必须一致）。绘制过程中的实时预览复用同一张位图，
    /// 避免每一帧都新建位图造成大量非托管内存分配。
    /// </summary>
    public static void CopyInto(WriteableBitmap bitmap, Image<Bgra32> image)
    {
        var width = Math.Min(bitmap.PixelSize.Width, image.Width);
        var height = Math.Min(bitmap.PixelSize.Height, image.Height);
        var row = new byte[width * 4];

        using var fb = bitmap.Lock();
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
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
    }

    /// <summary>等比缩放到 <paramref name="maxSide"/> 以内后的像素数据（不产生 Avalonia 位图）。</summary>
    public static Image<Bgra32> ToThumbnailImage(Image<Bgra32> image, int maxSide = 40)
    {
        var scale = Math.Min(
            (double)maxSide / Math.Max(1, image.Width),
            (double)maxSide / Math.Max(1, image.Height));
        scale = Math.Min(scale, 1.0);

        var w = Math.Max(1, (int)Math.Round(image.Width * scale));
        var h = Math.Max(1, (int)Math.Round(image.Height * scale));

        return ImageOps.Resize(image, w, h);
    }

    /// <summary>生成等比缩放到 <paramref name="maxSide"/> 以内的缩略图，用于图层列表。</summary>
    public static WriteableBitmap ToThumbnail(Image<Bgra32> image, int maxSide = 40)
    {
        using var scaled = ToThumbnailImage(image, maxSide);
        return ToBitmap(scaled);
    }
}
