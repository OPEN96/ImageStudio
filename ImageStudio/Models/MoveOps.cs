using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageStudio.Models;

/// <summary>
/// 图层内容的整数像素位移。
/// 核心是纯 span 运算（源缓冲 + 目标缓冲 + 可选选区覆盖度），
/// 这样一次拖动过程中可以反复把结果写进同一块缓冲做实时预览，不必每帧分配新图。
/// </summary>
public static class MoveOps
{
    /// <summary>把 <paramref name="source"/> 按 (dx, dy) 平移到 <paramref name="destination"/>。</summary>
    /// <remarks>
    /// 没有选区（<paramref name="coverage"/> 为空）时整幅平移，空出来的区域填透明；
    /// 有选区时只搬运覆盖度大于 0 的像素：原位按覆盖度减淡，目标位置按覆盖度做 source-over。
    /// </remarks>
    public static void Translate(
        ReadOnlySpan<Bgra32> source,
        Span<Bgra32> destination,
        int width,
        int height,
        int dx,
        int dy,
        ReadOnlySpan<byte> coverage = default)
    {
        if (width <= 0 || height <= 0)
            return;

        var count = width * height;
        if (source.Length < count || destination.Length < count)
            throw new ArgumentException("缓冲尺寸小于画布尺寸。", nameof(destination));

        if (coverage.Length < count)
        {
            TranslateWhole(source, destination, width, height, dx, dy);
            return;
        }

        // 选区内的像素：先整体复制，再把“搬走的部分”从原位减淡，最后把内容画到目标位置。
        // 分两遍是为了让“原地与新址重叠”的情况也得到正确结果。
        source[..count].CopyTo(destination);

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var c = coverage[row + x];
                if (c == 0)
                    continue;

                var pixel = source[row + x];
                if (pixel.A == 0)
                    continue;

                destination[row + x] = new Bgra32(
                    pixel.R, pixel.G, pixel.B, (byte)(pixel.A * (255 - c) / 255));
            }
        }

        for (var y = 0; y < height; y++)
        {
            var ty = y + dy;
            if (ty < 0 || ty >= height)
                continue;

            var row = y * width;
            var targetRow = ty * width;

            for (var x = 0; x < width; x++)
            {
                var c = coverage[row + x];
                if (c == 0)
                    continue;

                var tx = x + dx;
                if (tx < 0 || tx >= width)
                    continue;

                var pixel = source[row + x];
                if (pixel.A == 0)
                    continue;

                var alpha = pixel.A * c / 255;
                destination[targetRow + tx] = Over(destination[targetRow + tx], pixel, alpha);
            }
        }
    }

    private static void TranslateWhole(
        ReadOnlySpan<Bgra32> source, Span<Bgra32> destination, int width, int height, int dx, int dy)
    {
        var count = width * height;
        destination[..count].Clear();

        var x0 = Math.Max(0, -dx);
        var x1 = Math.Min(width, width - dx);
        if (x1 <= x0)
            return;

        var length = x1 - x0;
        for (var y = 0; y < height; y++)
        {
            var sy = y - dy;
            if (sy < 0 || sy >= height)
                continue;

            source.Slice(sy * width + x0, length)
                  .CopyTo(destination.Slice(y * width + x0 + dx, length));
        }
    }

    /// <summary>直通 alpha 的 source-over，把 <paramref name="source"/> 以 <paramref name="alpha"/> 叠到 <paramref name="destination"/> 上。</summary>
    private static Bgra32 Over(Bgra32 destination, Bgra32 source, int alpha)
    {
        if (alpha <= 0)
            return destination;

        var da = destination.A;
        var outA = alpha + da * (255 - alpha) / 255;
        if (outA <= 0)
            return default;

        var backWeight = da * (255 - alpha) / 255;
        return new Bgra32(
            (byte)Math.Min(255, (source.R * alpha + destination.R * backWeight) / outA),
            (byte)Math.Min(255, (source.G * alpha + destination.G * backWeight) / outA),
            (byte)Math.Min(255, (source.B * alpha + destination.B * backWeight) / outA),
            (byte)outA);
    }

    /// <summary>蒙版同样跟随内容移动：无选区时整幅平移（空出为 0），有选区时按覆盖度搬运。</summary>
    public static void TranslateMask(
        ReadOnlySpan<L8> source,
        Span<L8> destination,
        int width,
        int height,
        int dx,
        int dy,
        ReadOnlySpan<byte> coverage = default)
    {
        if (width <= 0 || height <= 0)
            return;

        var count = width * height;
        if (source.Length < count || destination.Length < count)
            throw new ArgumentException("缓冲尺寸小于画布尺寸。", nameof(destination));

        if (coverage.Length < count)
        {
            destination[..count].Clear();

            var x0 = Math.Max(0, -dx);
            var x1 = Math.Min(width, width - dx);
            if (x1 <= x0)
                return;

            var length = x1 - x0;
            for (var y = 0; y < height; y++)
            {
                var sy = y - dy;
                if (sy < 0 || sy >= height)
                    continue;

                source.Slice(sy * width + x0, length)
                      .CopyTo(destination.Slice(y * width + x0 + dx, length));
            }

            return;
        }

        source[..count].CopyTo(destination);

        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var c = coverage[row + x];
                if (c == 0)
                    continue;

                var value = source[row + x].PackedValue;
                destination[row + x] = new L8((byte)(value * (255 - c) / 255));
            }
        }

        for (var y = 0; y < height; y++)
        {
            var ty = y + dy;
            if (ty < 0 || ty >= height)
                continue;

            var row = y * width;
            var targetRow = ty * width;

            for (var x = 0; x < width; x++)
            {
                var c = coverage[row + x];
                if (c == 0)
                    continue;

                var tx = x + dx;
                if (tx < 0 || tx >= width)
                    continue;

                var moved = source[row + x].PackedValue * c / 255;
                var current = destination[targetRow + tx].PackedValue;
                destination[targetRow + tx] = new L8((byte)Math.Min(255, current + moved));
            }
        }
    }

    // ------------------------------------------------------------------ 便捷封装（一次性分配，用于提交与测试）

    /// <summary>平移整幅图并返回新图（<paramref name="clip"/> 为空则整层平移）。</summary>
    public static Image<Bgra32> Translate(Image<Bgra32> source, int dx, int dy, Selection? clip = null)
    {
        var width = source.Width;
        var height = source.Height;
        var pixels = new Bgra32[width * height];
        source.CopyPixelDataTo(pixels);

        var result = new Bgra32[width * height];
        Translate(pixels, result, width, height, dx, dy, CoverageOf(clip, width, height));
        return Image.LoadPixelData<Bgra32>(result, width, height);
    }

    /// <summary>平移蒙版并返回新蒙版。</summary>
    public static Image<L8> TranslateMask(Image<L8> source, int dx, int dy, Selection? clip = null)
    {
        var width = source.Width;
        var height = source.Height;
        var values = new L8[width * height];
        source.CopyPixelDataTo(values);

        var result = new L8[width * height];
        TranslateMask(values, result, width, height, dx, dy, CoverageOf(clip, width, height));
        return Image.LoadPixelData<L8>(result, width, height);
    }

    private static ReadOnlySpan<byte> CoverageOf(Selection? clip, int width, int height) =>
        clip is { IsEmpty: false } && clip.Width == width && clip.Height == height
            ? clip.Coverage
            : default;
}
