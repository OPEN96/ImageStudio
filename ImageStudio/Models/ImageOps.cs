using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ImageStudio.Models;

/// <summary>所有像素级处理都在这里完成，返回新的 <see cref="Image{Bgra32}"/>，不修改原图。</summary>
public static class ImageOps
{
    private static Bgra32 Pixel(byte r, byte g, byte b, byte a = 255) => new() { R = r, G = g, B = b, A = a };

    private static byte Clamp(double v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : (byte)Math.Round(v));

    // ---------------------------------------------------------------- 几何变换
    // 以下操作为纯索引重排，写成泛型以便同时作用于 Bgra32 像素与 L8 蒙版。

    public static Image<TPixel> Crop<TPixel>(Image<TPixel> src, Rectangle rect)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        rect = Rectangle.Intersect(rect, new Rectangle(0, 0, src.Width, src.Height));
        if (rect.Width <= 0 || rect.Height <= 0)
            throw new ArgumentException("裁剪区域为空。", nameof(rect));

        var dst = new Image<TPixel>(rect.Width, rect.Height);
        for (var y = 0; y < rect.Height; y++)
        for (var x = 0; x < rect.Width; x++)
            dst[x, y] = src[rect.X + x, rect.Y + y];
        return dst;
    }

    /// <summary>顺时针 / 逆时针旋转 90°。</summary>
    public static Image<TPixel> RotateQuarter<TPixel>(Image<TPixel> src, bool clockwise)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var w = src.Width;
        var h = src.Height;
        var dst = new Image<TPixel>(h, w);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (clockwise)
                dst[h - 1 - y, x] = src[x, y];
            else
                dst[y, w - 1 - x] = src[x, y];
        }
        return dst;
    }

    public static Image<TPixel> Rotate180<TPixel>(Image<TPixel> src)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var w = src.Width;
        var h = src.Height;
        var dst = new Image<TPixel>(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            dst[w - 1 - x, h - 1 - y] = src[x, y];
        return dst;
    }

    public static Image<TPixel> Flip<TPixel>(Image<TPixel> src, bool horizontal)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        var w = src.Width;
        var h = src.Height;
        var dst = new Image<TPixel>(w, h);
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (horizontal)
                dst[w - 1 - x, y] = src[x, y];
            else
                dst[x, h - 1 - y] = src[x, y];
        }
        return dst;
    }

    /// <summary>任意角度旋转，画布自动扩展，空白填透明。</summary>
    public static Image<Bgra32> RotateArbitrary(Image<Bgra32> src, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);

        var w = src.Width;
        var h = src.Height;
        var nw = Math.Max(1, (int)Math.Ceiling(Math.Abs(w * cos) + Math.Abs(h * sin)));
        var nh = Math.Max(1, (int)Math.Ceiling(Math.Abs(w * sin) + Math.Abs(h * cos)));

        var dst = new Image<Bgra32>(nw, nh);
        var cx = (w - 1) / 2.0;
        var cy = (h - 1) / 2.0;
        var ncx = (nw - 1) / 2.0;
        var ncy = (nh - 1) / 2.0;

        for (var y2 = 0; y2 < nh; y2++)
        for (var x2 = 0; x2 < nw; x2++)
        {
            var dx = x2 - ncx;
            var dy = y2 - ncy;
            var sx = cos * dx + sin * dy + cx;
            var sy = -sin * dx + cos * dy + cy;
            dst[x2, y2] = SampleBilinear(src, sx, sy);
        }

        return dst;
    }

    private static Bgra32 SampleBilinear(Image<Bgra32> src, double x, double y)
    {
        var x0 = (int)Math.Floor(x);
        var y0 = (int)Math.Floor(y);
        var fx = x - x0;
        var fy = y - y0;

        if (x0 < -1 || y0 < -1 || x0 > src.Width || y0 > src.Height)
            return default;

        double r = 0, g = 0, b = 0, a = 0;
        for (var dy = 0; dy <= 1; dy++)
        for (var dx = 0; dx <= 1; dx++)
        {
            var px = Math.Clamp(x0 + dx, 0, src.Width - 1);
            var py = Math.Clamp(y0 + dy, 0, src.Height - 1);
            var weight = (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy);
            var p = src[px, py];
            r += p.R * weight;
            g += p.G * weight;
            b += p.B * weight;
            a += p.A * weight;
        }

        return Pixel(Clamp(r), Clamp(g), Clamp(b), Clamp(a));
    }

    /// <summary>蒙版用的任意角度旋转（最近邻，输出尺寸与像素旋转严格一致）。</summary>
    public static Image<L8> RotateArbitraryMask(Image<L8> src, double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        var cos = Math.Cos(rad);
        var sin = Math.Sin(rad);

        var w = src.Width;
        var h = src.Height;
        var nw = Math.Max(1, (int)Math.Ceiling(Math.Abs(w * cos) + Math.Abs(h * sin)));
        var nh = Math.Max(1, (int)Math.Ceiling(Math.Abs(w * sin) + Math.Abs(h * cos)));

        var dst = new Image<L8>(nw, nh);
        var cx = (w - 1) / 2.0;
        var cy = (h - 1) / 2.0;
        var ncx = (nw - 1) / 2.0;
        var ncy = (nh - 1) / 2.0;

        for (var y2 = 0; y2 < nh; y2++)
        for (var x2 = 0; x2 < nw; x2++)
        {
            var dx = x2 - ncx;
            var dy = y2 - ncy;
            var sx = (int)Math.Round(cos * dx + sin * dy + cx);
            var sy = (int)Math.Round(-sin * dx + cos * dy + cy);

            dst[x2, y2] = sx < 0 || sy < 0 || sx >= w || sy >= h
                ? new L8(0)
                : src[sx, sy];
        }

        return dst;
    }

    public static Image<TPixel> Resize<TPixel>(Image<TPixel> src, int width, int height)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        return src.Clone(ctx => ctx.Resize(width, height, KnownResamplers.Lanczos3));
    }

    // ---------------------------------------------------------------- 色彩调整

    /// <summary>
    /// 亮度 / 对比度 / 饱和度，参数范围 -1..1，0 表示不变。
    /// 这里使用直观的加性亮度与围绕中灰的对比度增益（ImageSharp 自带的 Brightness 是乘性的且不接受负值）。
    /// </summary>
    public static Image<Bgra32> Adjust(Image<Bgra32> src, double brightness, double contrast, double saturation)
    {
        var width = src.Width;
        var height = src.Height;
        var dst = new Image<Bgra32>(width, height);

        var offset = brightness * 200.0;
        var contrastFactor = contrast >= 0 ? 1 + contrast * 1.6 : 1 + contrast * 0.8;
        var saturationFactor = 1 + saturation;

        var applyBrightness = Math.Abs(brightness) > 0.0005;
        var applyContrast = Math.Abs(contrast) > 0.0005;
        var applySaturation = Math.Abs(saturation) > 0.0005;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var p = src[x, y];
                double r = p.R, g = p.G, b = p.B;

                if (applySaturation)
                {
                    var luma = 0.299 * r + 0.587 * g + 0.114 * b;
                    r = luma + (r - luma) * saturationFactor;
                    g = luma + (g - luma) * saturationFactor;
                    b = luma + (b - luma) * saturationFactor;
                }

                if (applyContrast)
                {
                    r = (r - 127.5) * contrastFactor + 127.5;
                    g = (g - 127.5) * contrastFactor + 127.5;
                    b = (b - 127.5) * contrastFactor + 127.5;
                }

                if (applyBrightness)
                {
                    r += offset;
                    g += offset;
                    b += offset;
                }

                dst[x, y] = Pixel(Clamp(r), Clamp(g), Clamp(b), p.A);
            }
        }

        return dst;
    }

    public static Image<Bgra32> Grayscale(Image<Bgra32> src) => src.Clone(ctx => ctx.Grayscale());

    public static Image<Bgra32> Invert(Image<Bgra32> src) => src.Clone(ctx => ctx.Invert());

    public static Image<Bgra32> Blur(Image<Bgra32> src, float radius) =>
        src.Clone(ctx => ctx.GaussianBlur(Math.Max(0.1f, radius)));

    public static Image<Bgra32> Sharpen(Image<Bgra32> src, float amount) =>
        src.Clone(ctx => ctx.GaussianSharpen(Math.Max(0.1f, amount)));

    /// <summary>棕褐色调。</summary>
    public static Image<Bgra32> Sepia(Image<Bgra32> src)
    {
        var dst = new Image<Bgra32>(src.Width, src.Height);
        for (var y = 0; y < src.Height; y++)
        for (var x = 0; x < src.Width; x++)
        {
            var p = src[x, y];
            dst[x, y] = Pixel(
                Clamp(0.393 * p.R + 0.769 * p.G + 0.189 * p.B),
                Clamp(0.349 * p.R + 0.686 * p.G + 0.168 * p.B),
                Clamp(0.272 * p.R + 0.534 * p.G + 0.131 * p.B),
                p.A);
        }
        return dst;
    }

    /// <summary>Sobel 边缘检测。</summary>
    public static Image<Bgra32> Edges(Image<Bgra32> src)
    {
        var w = src.Width;
        var h = src.Height;
        var dst = new Image<Bgra32>(w, h);

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            if (x == 0 || y == 0 || x == w - 1 || y == h - 1)
            {
                dst[x, y] = Pixel(0, 0, 0, src[x, y].A);
                continue;
            }

            var gx = -Lum(src[x - 1, y - 1]) - 2 * Lum(src[x - 1, y]) - Lum(src[x - 1, y + 1])
                     + Lum(src[x + 1, y - 1]) + 2 * Lum(src[x + 1, y]) + Lum(src[x + 1, y + 1]);
            var gy = -Lum(src[x - 1, y - 1]) - 2 * Lum(src[x, y - 1]) - Lum(src[x + 1, y - 1])
                     + Lum(src[x - 1, y + 1]) + 2 * Lum(src[x, y + 1]) + Lum(src[x + 1, y + 1]);

            var magnitude = Clamp(Math.Sqrt(gx * gx + gy * gy));
            dst[x, y] = Pixel(magnitude, magnitude, magnitude, src[x, y].A);
        }

        return dst;
    }

    private static double Lum(Bgra32 p) => 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
}
