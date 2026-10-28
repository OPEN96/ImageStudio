using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageStudio.Models;

/// <summary>
/// 把图层栈自下而上合成为一张位图。
/// 采用 W3C Compositing 的 source-over + 混合函数公式，支持图层不透明度与 8 位蒙版。
/// </summary>
public static class LayerCompositor
{
    /// <summary>
    /// 合成整个图层栈。返回的新图由调用方负责释放。
    /// <paramref name="overrideIndex"/> 用于实时预览：合成到该层时改用 <paramref name="overridePixels"/>，
    /// 而不改动图层本身的像素。
    /// </summary>
    public static Image<Bgra32> Composite(
        int width,
        int height,
        IReadOnlyList<Layer> layers,
        int overrideIndex = -1,
        Image<Bgra32>? overridePixels = null,
        Image<L8>? overrideMask = null)
    {
        var dst = new Image<Bgra32>(Math.Max(1, width), Math.Max(1, height));

        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (!layer.IsVisible || layer.Opacity <= 0.0005)
                continue;

            var src = i == overrideIndex && overridePixels is not null ? overridePixels : layer.Pixels;

            // 尺寸不一致说明图层与画布脱节，跳过以免越界。
            if (src.Width != dst.Width || src.Height != dst.Height)
                continue;

            var mask = i == overrideIndex && overrideMask is not null ? overrideMask : layer.Mask;
            BlendLayer(dst, src, layer, mask);
        }

        return dst;
    }

    private static void BlendLayer(Image<Bgra32> dst, Image<Bgra32> src, Layer layer, Image<L8>? maskOverride)
    {
        var mode = layer.BlendMode;
        var opacity = Math.Clamp(layer.Opacity, 0, 1);
        var separable = BlendModes.Separable(mode);
        var nonSeparable = BlendModes.IsNonSeparable(mode);
        var mask = layer.MaskEnabled ? maskOverride : null;

        var width = dst.Width;
        var height = dst.Height;

        // 连续内存时按行并行；否则退回顺序遍历。
        if (TryGetBuffer(dst, out var dstMemory) && TryGetBuffer(src, out var srcMemory))
        {
            Memory<L8> maskMemory = default;
            var hasMask = mask is not null && TryGetBuffer(mask, out maskMemory);

            Parallel.For(0, height, y =>
            {
                var d = dstMemory.Span.Slice(y * width, width);
                var s = srcMemory.Span.Slice(y * width, width);

                if (hasMask)
                    BlitRow(d, s, maskMemory.Span.Slice(y * width, width), opacity, mode, separable, nonSeparable);
                else
                    BlitRow(d, s, default, opacity, mode, separable, nonSeparable);
            });

            return;
        }

        if (mask is null)
        {
            dst.ProcessPixelRows(src, (da, sa) =>
            {
                for (var y = 0; y < da.Height; y++)
                    BlitRow(da.GetRowSpan(y), sa.GetRowSpan(y), default, opacity, mode, separable, nonSeparable);
            });
        }
        else
        {
            dst.ProcessPixelRows(src, mask, (da, sa, ma) =>
            {
                for (var y = 0; y < da.Height; y++)
                    BlitRow(da.GetRowSpan(y), sa.GetRowSpan(y), ma.GetRowSpan(y), opacity, mode, separable, nonSeparable);
            });
        }
    }

    private static bool TryGetBuffer<TPixel>(Image<TPixel> image, out Memory<TPixel> memory)
        where TPixel : unmanaged, IPixel<TPixel>
    {
        if (image.DangerousTryGetSinglePixelMemory(out memory))
            return true;

        memory = default;
        return false;
    }

    private static void BlitRow(
        Span<Bgra32> dst,
        ReadOnlySpan<Bgra32> src,
        ReadOnlySpan<L8> mask,
        double opacity,
        BlendMode mode,
        Func<double, double, double>? separable,
        bool nonSeparable)
    {
        var hasMask = !mask.IsEmpty;

        for (var x = 0; x < dst.Length; x++)
        {
            var sp = src[x];
            if (sp.A == 0)
                continue;

            var aS = sp.A / 255.0 * opacity;
            if (hasMask)
                aS *= mask[x].PackedValue / 255.0;
            if (aS <= 0.0005)
                continue;

            var dp = dst[x];
            var aB = dp.A / 255.0;

            // 背景完全透明时任何混合模式都退化为源色本身
            if (aB <= 0.0005)
            {
                dst[x] = new Bgra32
                {
                    R = sp.R,
                    G = sp.G,
                    B = sp.B,
                    A = ToByte(aS * 255.0)
                };
                continue;
            }

            var aO = aS + aB * (1 - aS);
            var inv = 1.0 / aO;

            double cr, cg, cb;

            if (nonSeparable)
            {
                BlendModes.NonSeparable(
                    mode,
                    br: dp.R / 255.0, bg: dp.G / 255.0, bb: dp.B / 255.0,
                    sr: sp.R / 255.0, sg: sp.G / 255.0, sb: sp.B / 255.0,
                    r: out var blendedR, g: out var blendedG, b: out var blendedB);

                cr = Merge(dp.R / 255.0, sp.R / 255.0, blendedR, aS, aB) * inv;
                cg = Merge(dp.G / 255.0, sp.G / 255.0, blendedG, aS, aB) * inv;
                cb = Merge(dp.B / 255.0, sp.B / 255.0, blendedB, aS, aB) * inv;
            }
            else
            {
                var blend = separable!;
                cr = Merge(dp.R / 255.0, sp.R / 255.0, blend(dp.R / 255.0, sp.R / 255.0), aS, aB) * inv;
                cg = Merge(dp.G / 255.0, sp.G / 255.0, blend(dp.G / 255.0, sp.G / 255.0), aS, aB) * inv;
                cb = Merge(dp.B / 255.0, sp.B / 255.0, blend(dp.B / 255.0, sp.B / 255.0), aS, aB) * inv;
            }

            dst[x] = new Bgra32
            {
                R = ToByte(cr * 255.0),
                G = ToByte(cg * 255.0),
                B = ToByte(cb * 255.0),
                A = ToByte(aO * 255.0)
            };
        }
    }

    /// <summary>W3C 混合合成公式（未除以 αo 的分子部分）。</summary>
    private static double Merge(double cb, double cs, double blended, double aS, double aB) =>
        aS * (1 - aB) * cs + aS * aB * blended + (1 - aS) * aB * cb;

    private static byte ToByte(double v) =>
        v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)Math.Round(v);
}
