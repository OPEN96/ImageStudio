using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageStudio.Models;

/// <summary>
/// 一个图层：与画布等大的像素缓冲 + 可选的 8 位灰度蒙版。
/// 蒙版中 255 表示完全显示，0 表示完全隐藏。
/// </summary>
public sealed class Layer : IDisposable
{
    public Layer(string name, Image<Bgra32> pixels)
    {
        Name = name;
        Pixels = pixels;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Name { get; set; }

    public bool IsVisible { get; set; } = true;

    /// <summary>0..1。</summary>
    public double Opacity { get; set; } = 1;

    public BlendMode BlendMode { get; set; } = BlendMode.Normal;

    public Image<Bgra32> Pixels { get; set; }

    public Image<L8>? Mask { get; set; }

    public bool MaskEnabled { get; set; } = true;

    public bool HasMask => Mask is not null;

    public int Width => Pixels.Width;

    public int Height => Pixels.Height;

    /// <summary>像素内容的版本号，每次内容变化自增（用于判断缩略图是否需要重建）。</summary>
    public int ContentVersion { get; private set; }

    /// <summary>替换像素并释放旧的缓冲。</summary>
    public void ReplacePixels(Image<Bgra32> pixels)
    {
        if (ReferenceEquals(Pixels, pixels))
            return;

        Pixels.Dispose();
        Pixels = pixels;
        ContentVersion++;
    }

    /// <summary>替换蒙版并释放旧的缓冲（<paramref name="mask"/> 为空表示移除蒙版）。</summary>
    public void ReplaceMask(Image<L8>? mask)
    {
        if (ReferenceEquals(Mask, mask))
            return;

        Mask?.Dispose();
        Mask = mask;
        ContentVersion++;
    }

    /// <summary>对像素与蒙版同时施加几何变换（尺寸变化只允许各层一致）。</summary>
    public void Transform(
        Func<Image<Bgra32>, Image<Bgra32>> pixelOp,
        Func<Image<L8>, Image<L8>>? maskOp)
    {
        var newPixels = pixelOp(Pixels);
        if (!ReferenceEquals(newPixels, Pixels))
            Pixels.Dispose();
        Pixels = newPixels;
        ContentVersion++;

        if (Mask is null)
            return;

        if (maskOp is null)
        {
            Mask.Dispose();
            Mask = null;
            return;
        }

        var newMask = maskOp(Mask);
        if (!ReferenceEquals(newMask, Mask))
            Mask.Dispose();
        Mask = newMask;
        ContentVersion++;
    }

    public Layer Clone() => new(Name, Pixels.Clone())
    {
        IsVisible = IsVisible,
        Opacity = Opacity,
        BlendMode = BlendMode,
        Mask = Mask?.Clone(),
        MaskEnabled = MaskEnabled
    };

    /// <summary>复制出一份指定名字的副本（用于“复制图层”）。</summary>
    public Layer CloneAs(string name)
    {
        var clone = Clone();
        clone.Name = name;
        return clone;
    }

    public static Layer CreateEmpty(int width, int height, string name) =>
        new(name, new Image<Bgra32>(Math.Max(1, width), Math.Max(1, height)));

    /// <summary>用纯色填充整个图层。</summary>
    public static Layer CreateFilled(int width, int height, string name, Bgra32 color)
    {
        var image = new Image<Bgra32>(Math.Max(1, width), Math.Max(1, height));
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
                accessor.GetRowSpan(y).Fill(color);
        });
        return new Layer(name, image);
    }

    /// <summary>由 8 位灰度图创建蒙版。</summary>
    public static Image<L8> CreateMask(int width, int height, byte value = 255)
    {
        var mask = new Image<L8>(Math.Max(1, width), Math.Max(1, height));
        mask.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
                accessor.GetRowSpan(y).Fill(new L8(value));
        });
        return mask;
    }

    public void Dispose()
    {
        Pixels.Dispose();
        Mask?.Dispose();
        Mask = null;
    }
}
