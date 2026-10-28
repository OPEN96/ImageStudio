using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ImageStudio.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using AvaloniaColor = Avalonia.Media.Color;
using Point = Avalonia.Point;

namespace ImageStudio.Services;

/// <summary>
/// 文字工具的渲染。使用 Avalonia 的排版能力离屏绘制（保证与界面字体一致，含中文与字体回退），
/// 再把位图交给 ImageSharp 变成图层像素。必须由 UI 线程调用。
/// </summary>
public static class TextRenderer
{
    /// <summary>当前平台的默认字体（启动时从系统字体里挑出来的中文字体）。</summary>
    public static string DefaultFontFamily => FontCatalog.DefaultFamily;

    /// <summary>渲染文字并按左上角坐标贴进画布大小的透明图层；超出画布的部分被裁掉。</summary>
    public static Image<Bgra32>? RenderToLayer(
        string text,
        int canvasWidth,
        int canvasHeight,
        double x,
        double y,
        double fontSize,
        bool bold,
        string? fontFamily,
        AvaloniaColor color)
    {
        using var art = Render(text, fontSize, bold, fontFamily, color);
        if (art is null)
            return null;

        var width = Math.Max(1, canvasWidth);
        var height = Math.Max(1, canvasHeight);
        var destination = new Bgra32[width * height];

        var artPixels = new Bgra32[art.Width * art.Height];
        art.CopyPixelDataTo(artPixels);

        var left = (int)Math.Round(x);
        var top = (int)Math.Round(y);

        for (var ay = 0; ay < art.Height; ay++)
        {
            var dy = top + ay;
            if (dy < 0 || dy >= height)
                continue;

            var srcRow = ay * art.Width;
            var dstRow = dy * width;

            for (var ax = 0; ax < art.Width; ax++)
            {
                var dx = left + ax;
                if (dx < 0 || dx >= width)
                    continue;

                var pixel = artPixels[srcRow + ax];
                if (pixel.A == 0)
                    continue;

                destination[dstRow + dx] = pixel;
            }
        }

        return Image.LoadPixelData<Bgra32>(destination, width, height);
    }

    /// <summary>只渲染文字本身（尺寸贴合文字），供预览或粘贴使用。</summary>
    public static Image<Bgra32>? Render(
        string text,
        double fontSize,
        bool bold,
        string? fontFamily,
        AvaloniaColor color)
    {
        if (string.IsNullOrWhiteSpace(text) || fontSize < 1)
            return null;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var typeface = new Typeface(
            new FontFamily(string.IsNullOrWhiteSpace(fontFamily) ? DefaultFontFamily : fontFamily!),
            FontStyle.Normal,
            bold ? FontWeight.Bold : FontWeight.Normal);

        var brush = new SolidColorBrush(color);
        var lineHeight = Math.Ceiling(fontSize * 1.32);

        var formatted = new List<FormattedText>(lines.Length);
        foreach (var line in lines)
        {
            formatted.Add(new FormattedText(
                line.Length == 0 ? " " : line,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                fontSize,
                brush));
        }

        var textWidth = 0.0;
        foreach (var item in formatted)
            textWidth = Math.Max(textWidth, item.Width);

        var width = Math.Max(1, (int)Math.Ceiling(textWidth) + 4);
        var height = Math.Max(1, (int)Math.Ceiling(lineHeight * lines.Length) + 4);

        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));

        using (var context = bitmap.CreateDrawingContext())
        {
            for (var i = 0; i < formatted.Count; i++)
                context.DrawText(formatted[i], new Point(2, 2 + i * lineHeight));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;

        return Image.Load<Bgra32>(stream);
    }
}
