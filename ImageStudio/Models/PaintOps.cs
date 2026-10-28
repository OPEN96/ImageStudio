using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageStudio.Models;

/// <summary>渐变形状。</summary>
public enum GradientKind
{
    Linear,
    Radial
}

/// <summary>绘画类工具（油漆桶 / 渐变）的像素实现。</summary>
public static class PaintOps
{
    /// <summary>
    /// 油漆桶：以 <paramref name="sample"/>（通常是合成结果）为参考，在种子点扩散出相近颜色区域，
    /// 再用前景色填充 <paramref name="target"/> 上对应的像素。有选区时只填充与选区的交集。
    /// </summary>
    public static Image<Bgra32> BucketFill(
        Image<Bgra32> target,
        Image<Bgra32> sample,
        Selection? clip,
        int seedX,
        int seedY,
        double tolerance,
        bool contiguous,
        Bgra32 color)
    {
        if (sample.Width != target.Width || sample.Height != target.Height)
            return target.Clone();

        if (seedX < 0 || seedY < 0 || seedX >= sample.Width || seedY >= sample.Height)
            return target.Clone();

        var region = Selection.Wand(sample, seedX, seedY, tolerance, contiguous);

        if (clip is { IsEmpty: false } && clip.Width == target.Width && clip.Height == target.Height)
            region = region.Combine(clip, SelectionCombine.Intersect);

        return region.IsEmpty ? target.Clone() : ImageOps.FillByMask(target, region, color);
    }

    /// <summary>
    /// 渐变：从 (x0,y0) 到 (x1,y1)，颜色与透明度线性插值后叠加到原图。
    /// <paramref name="clip"/> 决定作用范围（与选区覆盖度相乘）。
    /// </summary>
    public static Image<Bgra32> Gradient(
        Image<Bgra32> source,
        Selection? clip,
        double x0,
        double y0,
        double x1,
        double y1,
        Bgra32 from,
        Bgra32 to,
        GradientKind kind)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var lengthSquared = dx * dx + dy * dy;

        if (lengthSquared < 1e-6)
            return source.Clone();

        var length = Math.Sqrt(lengthSquared);
        var width = source.Width;
        var height = source.Height;

        var pixels = new Bgra32[width * height];
        source.CopyPixelDataTo(pixels);

        var coverage = clip is { IsEmpty: false } && clip.Width == width && clip.Height == height
            ? clip.Coverage
            : null;

        for (var y = 0; y < height; y++)
        {
            var py = y + 0.5;
            var row = y * width;

            for (var x = 0; x < width; x++)
            {
                var index = row + x;

                var mask = 1.0;
                if (coverage is not null)
                {
                    var c = coverage[index];
                    if (c == 0)
                        continue;
                    mask = c / 255.0;
                }

                var px = x + 0.5;
                double t;

                if (kind == GradientKind.Linear)
                {
                    t = ((px - x0) * dx + (py - y0) * dy) / lengthSquared;
                }
                else
                {
                    var ddx = px - x0;
                    var ddy = py - y0;
                    t = Math.Sqrt(ddx * ddx + ddy * ddy) / length;
                }

                t = Math.Clamp(t, 0, 1);

                var red = Lerp(from.R, to.R, t);
                var green = Lerp(from.G, to.G, t);
                var blue = Lerp(from.B, to.B, t);
                var alpha = Lerp(from.A, to.A, t) / 255.0;

                var sourceAlpha = alpha * mask;
                if (sourceAlpha <= 0.0005)
                    continue;

                pixels[index] = Over(pixels[index], red, green, blue, sourceAlpha);
            }
        }

        return Image.LoadPixelData<Bgra32>(pixels, width, height);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>把半透明的直通 alpha 颜色叠加到目标像素上（source-over）。</summary>
    private static Bgra32 Over(Bgra32 under, double red, double green, double blue, double sourceAlpha)
    {
        var underAlpha = under.A / 255.0;
        var outAlpha = sourceAlpha + underAlpha * (1 - sourceAlpha);

        if (outAlpha <= 0)
            return default;

        var k = 1.0 / outAlpha;
        var underWeight = underAlpha * (1 - sourceAlpha) * k;

        return new Bgra32
        {
            R = Clamp(red * sourceAlpha * k + under.R * underWeight),
            G = Clamp(green * sourceAlpha * k + under.G * underWeight),
            B = Clamp(blue * sourceAlpha * k + under.B * underWeight),
            A = Clamp(outAlpha * 255)
        };
    }

    private static byte Clamp(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)Math.Round(v);
}
