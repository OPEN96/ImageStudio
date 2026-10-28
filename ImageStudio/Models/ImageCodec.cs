using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageStudio.Models;

public enum ImageFormatKind
{
    Png,
    Jpeg,
    Bmp,
    Gif,
    Webp
}

/// <summary>图像文件读写。</summary>
public static class ImageCodec
{
    public static string ExtensionOf(ImageFormatKind kind) => kind switch
    {
        ImageFormatKind.Jpeg => "jpg",
        ImageFormatKind.Bmp => "bmp",
        ImageFormatKind.Gif => "gif",
        ImageFormatKind.Webp => "webp",
        _ => "png"
    };

    public static ImageFormatKind KindFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" => ImageFormatKind.Jpeg,
        ".bmp" => ImageFormatKind.Bmp,
        ".gif" => ImageFormatKind.Gif,
        ".webp" => ImageFormatKind.Webp,
        _ => ImageFormatKind.Png
    };

    public static Image<Bgra32> Load(string path)
    {
        using var stream = File.OpenRead(path);
        return SixLabors.ImageSharp.Image.Load<Bgra32>(stream);
    }

    public static void Save(Image<Bgra32> image, string path, ImageFormatKind kind, int quality)
    {
        IImageEncoder encoder = kind switch
        {
            ImageFormatKind.Jpeg => new JpegEncoder { Quality = Math.Clamp(quality, 1, 100) },
            ImageFormatKind.Bmp => new BmpEncoder(),
            ImageFormatKind.Gif => new GifEncoder(),
            ImageFormatKind.Webp => new WebpEncoder { Quality = Math.Clamp(quality, 1, 100) },
            _ => new PngEncoder()
        };

        using var stream = File.Create(path);
        image.Save(stream, encoder);
    }

    /// <summary>按照文件路径扩展名推荐保存格式。</summary>
    public static ImageFormatKind GuessKind(string path, ImageFormatKind fallback)
    {
        var ext = Path.GetExtension(path);
        return string.IsNullOrEmpty(ext) ? fallback : KindFromPath(path);
    }
}
