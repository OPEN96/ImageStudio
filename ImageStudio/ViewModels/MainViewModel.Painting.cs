using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Controls;
using ImageStudio.Models;
using ImageStudio.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// Avalonia 与 ImageSharp 存在同名类型，这里显式指定使用 Avalonia 的类型。
using Color = Avalonia.Media.Color;
using Point = Avalonia.Point;

namespace ImageStudio.ViewModels;

/// <summary>绘画类工具（画笔 / 橡皮 / 油漆桶 / 渐变 / 文字）的状态与命令。</summary>
public partial class MainViewModel
{
    private const double PreviewIntervalMs = 32;

    private StrokeSession? _stroke;
    private long _lastPreviewTimestamp;
    private WriteableBitmap? _previewBitmap;

    // ------------------------------------------------------------------ 工具状态

    public bool IsBrushTool => ActiveTool == CanvasTool.Brush;

    public bool IsEraserTool => ActiveTool == CanvasTool.Eraser;

    public bool IsBrushActive => IsBrushTool || IsEraserTool;

    public bool IsBucketTool => ActiveTool == CanvasTool.Bucket;

    public bool IsGradientTool => ActiveTool == CanvasTool.Gradient;

    public bool IsTextTool => ActiveTool == CanvasTool.Text;

    /// <summary>是否处于任一绘制类工具下（用于显示右侧提示）。</summary>
    public bool IsPaintToolActive => IsBrushActive || IsBucketTool || IsGradientTool || IsTextTool;

    private void NotifyPaintToolState()
    {
        OnPropertyChanged(nameof(IsBrushTool));
        OnPropertyChanged(nameof(IsEraserTool));
        OnPropertyChanged(nameof(IsBrushActive));
        OnPropertyChanged(nameof(IsBucketTool));
        OnPropertyChanged(nameof(IsGradientTool));
        OnPropertyChanged(nameof(IsTextTool));
        OnPropertyChanged(nameof(IsPaintToolActive));
    }

    // ------------------------------------------------------------------ 画笔参数

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrushSizeText))]
    public partial double BrushSize { get; set; } = 24;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrushHardnessText))]
    public partial double BrushHardness { get; set; } = 80;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrushOpacityText))]
    public partial double BrushOpacity { get; set; } = 100;

    public string BrushSizeText => $"{BrushSize:0} px";

    public string BrushHardnessText => $"{BrushHardness:0}%";

    public string BrushOpacityText => $"{BrushOpacity:0}%";

    public void AdjustBrushSize(double factor)
    {
        BrushSize = Math.Clamp(Math.Round(BrushSize * factor), 1, 400);
        StatusText = $"画笔大小 {BrushSize:0} px";
    }

    // ------------------------------------------------------------------ 油漆桶

    [ObservableProperty]
    public partial double BucketTolerance { get; set; } = 32;

    [ObservableProperty]
    public partial bool BucketContiguous { get; set; } = true;

    // ------------------------------------------------------------------ 渐变

    [ObservableProperty]
    public partial bool IsLinearGradient { get; set; } = true;

    public bool IsRadialGradient => !IsLinearGradient;

    [ObservableProperty]
    public partial bool GradientToTransparent { get; set; } = true;

    partial void OnIsLinearGradientChanged(bool value) => OnPropertyChanged(nameof(IsRadialGradient));

    public void SetGradientKind(bool linear)
    {
        IsLinearGradient = linear;
        StatusText = linear ? "渐变：线性" : "渐变：径向";
    }

    // ------------------------------------------------------------------ 背景色

    [ObservableProperty]
    public partial Color BackColor { get; set; } = Color.FromRgb(0x1A, 0x1A, 0x20);

    [ObservableProperty]
    public partial string BackHex { get; set; } = "1A1A20";

    public IBrush BackBrush => new SolidColorBrush(BackColor);

    partial void OnBackColorChanged(Color value) => OnPropertyChanged(nameof(BackBrush));

    partial void OnBackHexChanged(string value)
    {
        if (TryParseHex(value, out var color))
            BackColor = color;
    }

    /// <summary>交换前景色与背景色（快捷键 X）。</summary>
    [RelayCommand]
    private void SwapColors()
    {
        var foreground = FillColor;
        var background = BackColor;

        SetFillColor(background);

        BackColor = foreground;
        BackHex = $"{foreground.R:X2}{foreground.G:X2}{foreground.B:X2}";

        StatusText = $"前景 #{FillHex} · 背景 #{BackHex}";
    }

    private static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var value = text.Trim().TrimStart('#');
        if (value.Length == 3)
            value = string.Concat(value.Select(c => new string(c, 2)));

        if (value.Length != 6 ||
            !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
            return false;

        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    // ------------------------------------------------------------------ 文字参数

    [ObservableProperty]
    public partial double TextFontSize { get; set; } = 72;

    [ObservableProperty]
    public partial bool TextBold { get; set; }

    [ObservableProperty]
    public partial string TextFontFamily { get; set; } = FontCatalog.DefaultFamily;

    // ------------------------------------------------------------------ 笔画生命周期

    /// <summary>画笔 / 橡皮按下：在活动图层上开一笔。</summary>
    public void OnPaintStart(Point position)
    {
        if (_document?.ActiveLayer is not { } layer || _isBusy)
            return;

        if (layer.Width != _document.Width || layer.Height != _document.Height)
            return;

        _stroke?.Dispose();

        var erase = ActiveTool == CanvasTool.Eraser;
        var brush = new BrushSettings
        {
            Size = BrushSize,
            Hardness = BrushHardness / 100.0,
            Opacity = BrushOpacity / 100.0,
            Erase = erase
        };

        _stroke = new StrokeSession(layer.Pixels, brush, ToBgra32(FillColor), _selection);
        _stroke.MoveTo(position.X, position.Y);
        PushStrokePreview(force: true);
    }

    /// <summary>画笔 / 橡皮拖动。</summary>
    public void OnPaintMove(Point position)
    {
        if (_stroke is null)
            return;

        _stroke.MoveTo(position.X, position.Y);
        PushStrokePreview(force: false);
    }

    /// <summary>画笔 / 橡皮抬起：把结果提交为一个可撤销步骤。</summary>
    public void OnPaintEnd()
    {
        var stroke = _stroke;
        _stroke = null;

        if (stroke is null)
            return;

        try
        {
            if (!stroke.HasPaint || _document?.ActiveLayer is not { } layer)
                return;

            var label = ActiveTool == CanvasTool.Eraser ? "橡皮擦" : "画笔";

            using (_document.BeginEdit(label))
                layer.ReplacePixels(stroke.TakePixels());

            StatusText = $"{label} 已完成";
        }
        catch (Exception ex)
        {
            StatusText = "绘制失败：" + ex.Message;
        }
        finally
        {
            stroke.Dispose();
            RefreshCommandStates();
        }
    }

    /// <summary>
    /// 绘制过程的实时预览：把当前笔画合成进图层栈。
    /// 复用同一张位图，靠 SourceVersion 触发重绘，避免每帧分配大块非托管内存。
    /// </summary>
    private void PushStrokePreview(bool force)
    {
        if (_document is null || _stroke is null)
            return;

        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastPreviewTimestamp) * 1000.0 / Stopwatch.Frequency;
        if (!force && elapsed < PreviewIntervalMs)
            return;

        _lastPreviewTimestamp = now;

        try
        {
            using var composed = _document.CompositeWith(_document.ActiveIndex, _stroke.Pixels);

            if (_previewBitmap is null ||
                _previewBitmap.PixelSize.Width != composed.Width ||
                _previewBitmap.PixelSize.Height != composed.Height)
            {
                _previewBitmap?.Dispose();
                _previewBitmap = BitmapFactory.ToBitmap(composed);
                ImageSource = _previewBitmap;
                return;
            }

            BitmapFactory.CopyInto(_previewBitmap, composed);

            if (!ReferenceEquals(ImageSource, _previewBitmap))
                ImageSource = _previewBitmap;

            SourceVersion++;
        }
        catch (Exception ex)
        {
            StatusText = "预览失败：" + ex.Message;
        }
    }

    // ------------------------------------------------------------------ 油漆桶 / 渐变 / 文字

    /// <summary>油漆桶：以合成结果为参考扩散相近颜色，只填充当前图层。</summary>
    public async Task OnBucketAsync(Point position)
    {
        if (_document?.ActiveLayer is not { } layer || _isBusy)
            return;

        var x = (int)Math.Floor(position.X);
        var y = (int)Math.Floor(position.Y);

        if (x < 0 || y < 0 || x >= _document.Width || y >= _document.Height)
            return;

        var color = ToBgra32(FillColor);
        var tolerance = BucketTolerance;
        var contiguous = BucketContiguous;
        var selection = _selection;

        _isBusy = true;
        IsBusy = true;

        try
        {
            var sample = _document.Composite().Clone();

            using (_document.BeginEdit("油漆桶"))
            {
                var result = await Task.Run(() =>
                {
                    using (sample)
                        return PaintOps.BucketFill(layer.Pixels, sample, selection, x, y, tolerance, contiguous, color);
                });

                layer.ReplacePixels(result);
            }

            StatusText = $"油漆桶 · 容差 {tolerance:0} · #{FillHex}";
        }
        catch (Exception ex)
        {
            StatusText = "填充失败：" + ex.Message;
        }
        finally
        {
            _isBusy = false;
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    /// <summary>渐变：从拖拽起点到终点铺开前景色（可渐变到透明或背景色）。</summary>
    public async Task OnGradientAsync(Point start, Point end)
    {
        if (_document?.ActiveLayer is not { } layer || _isBusy)
            return;

        var from = ToBgra32(FillColor);
        var to = GradientToTransparent
            ? new Bgra32 { R = from.R, G = from.G, B = from.B, A = 0 }
            : ToBgra32(BackColor);

        var kind = IsLinearGradient ? GradientKind.Linear : GradientKind.Radial;
        var selection = _selection;

        _isBusy = true;
        IsBusy = true;

        try
        {
            using (_document.BeginEdit(kind == GradientKind.Linear ? "线性渐变" : "径向渐变"))
            {
                var result = await Task.Run(() => PaintOps.Gradient(
                    layer.Pixels, selection,
                    start.X, start.Y, end.X, end.Y,
                    from, to, kind));

                layer.ReplacePixels(result);
            }

            StatusText = $"{(kind == GradientKind.Linear ? "线性" : "径向")}渐变已完成";
        }
        catch (Exception ex)
        {
            StatusText = "渐变失败：" + ex.Message;
        }
        finally
        {
            _isBusy = false;
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    /// <summary>文字工具点击画布：弹窗输入后建立文字图层。</summary>
    public async Task OnTextAsync(Point position)
    {
        if (_document is null || Owner is null)
            return;

        var dialog = new Views.TextToolDialog(FillColor, TextFontSize, TextBold, TextFontFamily, string.Empty);
        if (!await dialog.ShowDialog<bool>(Owner))
            return;

        TextFontSize = dialog.ResultFontSize;
        TextBold = dialog.ResultBold;
        TextFontFamily = dialog.ResultFontFamily;

        AddTextLayer(position, dialog.ResultText, dialog.ResultFontSize, dialog.ResultBold, dialog.ResultFontFamily);
    }

    /// <summary>把文字渲染成新图层（也是“添加文字”按钮的入口）。</summary>
    public void AddTextLayer(Point position, string text, double fontSize, bool bold, string fontFamily)
    {
        if (_document is null)
            return;

        try
        {
            var pixels = TextRenderer.RenderToLayer(
                text, _document.Width, _document.Height,
                position.X, position.Y,
                fontSize, bold, fontFamily, FillColor);

            if (pixels is null)
            {
                StatusText = "文字内容为空";
                return;
            }

            var name = text.Length > 12 ? text[..12] + "…" : text.Replace('\n', ' ');
            _document.AddLayerFromPixels($"文字 {name}", pixels);
            StatusText = $"已添加文字图层（{fontSize:0} px）";
        }
        catch (Exception ex)
        {
            StatusText = "文字渲染失败：" + ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task AddTextAsync()
    {
        if (_document is null)
            return;

        // 没有点击位置时落在画布左侧偏上，保证文字基本可见
        var anchor = new Point(_document.Width * 0.08, _document.Height * 0.4);
        await OnTextAsync(anchor);
    }

    private static Bgra32 ToBgra32(Color color) =>
        new() { R = color.R, G = color.G, B = color.B, A = color.A };
}
