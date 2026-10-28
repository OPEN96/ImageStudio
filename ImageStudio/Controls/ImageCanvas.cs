using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ImageStudio.Models;

namespace ImageStudio.Controls;

/// <summary>画布当前的操作工具。</summary>
public enum CanvasTool
{
    Pan,
    Crop,
    SelectRect,
    SelectEllipse,
    Lasso,
    Wand,
    Brush,
    Eraser,
    Bucket,
    Gradient,
    Text
}

public sealed class CanvasPointerEventArgs(Point imagePosition, bool isInside) : EventArgs
{
    public Point ImagePosition { get; } = imagePosition;
    public bool IsInside { get; } = isInside;
}

public sealed class SelectionRectEventArgs(Rect rect, KeyModifiers modifiers) : EventArgs
{
    public Rect Rect { get; } = rect;
    public KeyModifiers Modifiers { get; } = modifiers;
}

public sealed class SelectionPathEventArgs(IReadOnlyList<Point> points, KeyModifiers modifiers) : EventArgs
{
    public IReadOnlyList<Point> Points { get; } = points;
    public KeyModifiers Modifiers { get; } = modifiers;
}

public sealed class WandPickEventArgs(Point position, KeyModifiers modifiers) : EventArgs
{
    public Point Position { get; } = position;
    public KeyModifiers Modifiers { get; } = modifiers;
}

/// <summary>绘制类工具的落点（图像像素坐标，可能略超出画布）。</summary>
public sealed class PaintEventArgs(Point position) : EventArgs
{
    public Point Position { get; } = position;
}

/// <summary>渐变拖拽的起止点（图像像素坐标）。</summary>
public sealed class GradientEventArgs(Point start, Point end) : EventArgs
{
    public Point Start { get; } = start;
    public Point End { get; } = end;
}

/// <summary>
/// 图像显示区：棋盘格背景、缩放、平移、裁剪框与选区交互。
/// </summary>
public class ImageCanvas : Control
{
    private const double MinZoom = 0.02;
    private const double MaxZoom = 40;

    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ImageCanvas, Bitmap?>(nameof(Source));

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<ImageCanvas, double>(
            nameof(Zoom), 1.0, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<Point> OffsetProperty =
        AvaloniaProperty.Register<ImageCanvas, Point>(
            nameof(Offset), default, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> ShowCheckerboardProperty =
        AvaloniaProperty.Register<ImageCanvas, bool>(nameof(ShowCheckerboard), true);

    public static readonly StyledProperty<CanvasTool> ToolProperty =
        AvaloniaProperty.Register<ImageCanvas, CanvasTool>(
            nameof(Tool), CanvasTool.Pan, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<Rect> CropRectProperty =
        AvaloniaProperty.Register<ImageCanvas, Rect>(
            nameof(CropRect), default, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<Selection?> SelectionProperty =
        AvaloniaProperty.Register<ImageCanvas, Selection?>(nameof(Selection));

    public static readonly StyledProperty<bool> ShowSelectionProperty =
        AvaloniaProperty.Register<ImageCanvas, bool>(nameof(ShowSelection), true);

    public static readonly StyledProperty<double> BrushSizeProperty =
        AvaloniaProperty.Register<ImageCanvas, double>(nameof(BrushSize), 24);

    /// <summary>
    /// 位图内容版本号。复用同一张位图刷新像素时，<see cref="Source"/> 引用不变，
    /// 靠这个计数器触发重绘。
    /// </summary>
    public static readonly StyledProperty<int> SourceVersionProperty =
        AvaloniaProperty.Register<ImageCanvas, int>(nameof(SourceVersion));

    private static readonly IBrush CanvasBackground = new SolidColorBrush(Color.Parse("#151519"));
    private static readonly IPen ImageBorderPen = new Pen(new SolidColorBrush(Color.Parse("#3D3D47")), 1);
    private static readonly IPen CropPen = new Pen(Brushes.White, 1.2);
    private static readonly IPen CropThirdsPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
    private static readonly IBrush OutsideFill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly IBrush HandleFill = Brushes.White;
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.Parse("#111114")), 1);
    private static readonly IBrush HintBrush = new SolidColorBrush(Color.Parse("#6E6E7A"));

    private static readonly IPen AntsUnderPen = new Pen(new SolidColorBrush(Color.Parse("#101014")), 1.7);
    private static readonly IPen PreviewPen = new Pen(Brushes.White, 1.1);
    private static readonly IPen PreviewUnderPen = new Pen(new SolidColorBrush(Color.Parse("#101014")), 1.9);

    private static readonly IPen BrushCursorPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)), 1);
    private static readonly IPen BrushCursorUnderPen =
        new Pen(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)), 2.4);
    private static readonly IPen GradientPen = new Pen(Brushes.White, 1.3, new DashStyle([5, 4], 0));
    private static readonly IPen GradientUnderPen = new Pen(new SolidColorBrush(Color.Parse("#101014")), 2.6);

    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

    private static ImageBrush? _checkerBrush;

    private readonly DispatcherTimer _antsTimer;

    private bool _isPanning;
    private bool _isCropping;
    private Point _lastPointer;
    private Point _cropAnchor;

    private bool _isSelectDragging;
    private Point _selectAnchor;
    private Point _selectCurrent;
    private List<Point>? _lassoPoints;
    private double _antsPhase;

    private bool _isPainting;
    private Point? _hover;
    private bool _isGradientDragging;
    private Point _gradientStart;
    private Point _gradientCurrent;

    private WriteableBitmap? _tintBitmap;
    private Selection? _tintSource;

    static ImageCanvas()
    {
        AffectsRender<ImageCanvas>(
            SourceProperty, ZoomProperty, OffsetProperty, ShowCheckerboardProperty,
            ToolProperty, CropRectProperty, SelectionProperty, ShowSelectionProperty,
            BrushSizeProperty, SourceVersionProperty);
        FocusableProperty.OverrideDefaultValue<ImageCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<ImageCanvas>(true);
    }

    public ImageCanvas()
    {
        Cursor = MoveCursor;

        _antsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _antsTimer.Tick += (_, _) =>
        {
            _antsPhase = (_antsPhase + 1) % 8;
            InvalidateVisual();
        };
    }

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public Point Offset
    {
        get => GetValue(OffsetProperty);
        set => SetValue(OffsetProperty, value);
    }

    public bool ShowCheckerboard
    {
        get => GetValue(ShowCheckerboardProperty);
        set => SetValue(ShowCheckerboardProperty, value);
    }

    public CanvasTool Tool
    {
        get => GetValue(ToolProperty);
        set => SetValue(ToolProperty, value);
    }

    public Rect CropRect
    {
        get => GetValue(CropRectProperty);
        set => SetValue(CropRectProperty, value);
    }

    public Selection? Selection
    {
        get => GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    public bool ShowSelection
    {
        get => GetValue(ShowSelectionProperty);
        set => SetValue(ShowSelectionProperty, value);
    }

    /// <summary>画笔直径（图像像素），用于绘制圆形光标。</summary>
    public double BrushSize
    {
        get => GetValue(BrushSizeProperty);
        set => SetValue(BrushSizeProperty, value);
    }

    public int SourceVersion
    {
        get => GetValue(SourceVersionProperty);
        set => SetValue(SourceVersionProperty, value);
    }

    public event EventHandler<CanvasPointerEventArgs>? PointerImageMoved;

    public event EventHandler<SelectionRectEventArgs>? RectSelectionCompleted;

    public event EventHandler<SelectionPathEventArgs>? LassoSelectionCompleted;

    public event EventHandler<WandPickEventArgs>? WandPickRequested;

    /// <summary>画笔 / 橡皮按下。</summary>
    public event EventHandler<PaintEventArgs>? PaintStarted;

    /// <summary>画笔 / 橡皮拖动。</summary>
    public event EventHandler<PaintEventArgs>? PaintMoved;

    /// <summary>画笔 / 橡皮抬起。</summary>
    public event EventHandler? PaintEnded;

    /// <summary>油漆桶点击。</summary>
    public event EventHandler<WandPickEventArgs>? BucketPickRequested;

    /// <summary>渐变拖拽结束。</summary>
    public event EventHandler<GradientEventArgs>? GradientCompleted;

    /// <summary>文字工具点击。</summary>
    public event EventHandler<PaintEventArgs>? TextRequested;

    // ------------------------------------------------------------------ 尺寸换算

    public Size ImagePixelSize => Source is { } bmp
        ? new Size(bmp.PixelSize.Width, bmp.PixelSize.Height)
        : default;

    public Rect ImageScreenRect => new(Offset, new Size(ImagePixelSize.Width * Zoom, ImagePixelSize.Height * Zoom));

    /// <summary>控件坐标 → 图像像素坐标。</summary>
    public Point ToImage(Point screen) => new(
        Zoom <= 0 ? 0 : (screen.X - Offset.X) / Zoom,
        Zoom <= 0 ? 0 : (screen.Y - Offset.Y) / Zoom);

    private Point ToScreen(Point image) => new(
        Offset.X + image.X * Zoom,
        Offset.Y + image.Y * Zoom);

    private Point ClampToImage(Point img)
    {
        var size = ImagePixelSize;
        return new Point(
            Math.Clamp(img.X, 0, size.Width),
            Math.Clamp(img.Y, 0, size.Height));
    }

    // ------------------------------------------------------------------ 渲染

    public override void Render(DrawingContext context)
    {
        var viewport = new Rect(Bounds.Size);
        context.FillRectangle(CanvasBackground, viewport);

        if (Source is not { } bitmap)
        {
            DrawEmptyHint(context, viewport);
            return;
        }

        var dest = ImageScreenRect;
        if (dest.Width < 1 || dest.Height < 1)
            return;

        if (ShowCheckerboard)
            context.FillRectangle(CheckerBrush, dest);

        using (context.PushClip(dest))
            context.DrawImage(bitmap, new Rect(bitmap.Size), dest);

        context.DrawRectangle(null, ImageBorderPen, dest);

        if (ShowSelection)
            DrawSelection(context, dest);

        if (Tool == CanvasTool.Crop)
            DrawCropOverlay(context, dest);

        DrawSelectionDragPreview(context);
        DrawGradientPreview(context);
        DrawBrushCursor(context);
    }

    /// <summary>画笔直径的圆形光标，缩放时跟随缩放。</summary>
    private void DrawBrushCursor(DrawingContext context)
    {
        if (!IsPaintTool || _hover is not { } hover)
            return;

        var radius = Math.Clamp(BrushSize / 2 * Zoom, 1.5, 4000);
        context.DrawEllipse(null, BrushCursorUnderPen, hover, radius, radius);
        context.DrawEllipse(null, BrushCursorPen, hover, radius, radius);

        if (radius > 9)
        {
            var arm = radius > 24 ? 5.0 : 3.0;
            context.DrawLine(BrushCursorUnderPen,
                new Point(hover.X - arm, hover.Y), new Point(hover.X + arm, hover.Y));
            context.DrawLine(BrushCursorUnderPen,
                new Point(hover.X, hover.Y - arm), new Point(hover.X, hover.Y + arm));
        }
    }

    private void DrawGradientPreview(DrawingContext context)
    {
        if (!_isGradientDragging)
            return;

        context.DrawLine(GradientUnderPen, _gradientStart, _gradientCurrent);
        context.DrawLine(GradientPen, _gradientStart, _gradientCurrent);

        const double r = 4;
        context.DrawEllipse(null, GradientUnderPen, _gradientStart, r + 1, r + 1);
        context.DrawEllipse(Brushes.White, GradientUnderPen, _gradientStart, r, r);
        context.DrawEllipse(Brushes.White, GradientUnderPen, _gradientCurrent, r, r);
    }

    private void DrawSelection(DrawingContext context, Rect imageRect)
    {
        if (Selection is not { } selection || selection.IsEmpty)
            return;

        var tint = GetTintBitmap(selection);
        if (tint is not null)
        {
            using (context.PushClip(imageRect))
                context.DrawImage(tint, new Rect(tint.Size), imageRect);
        }

        if (selection.GetOutline() is not { Count: > 0 } outline)
            return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            foreach (var loop in outline)
            {
                if (loop.Length < 2)
                    continue;

                ctx.BeginFigure(ToScreen(new Point(loop[0].X, loop[0].Y)), isFilled: false);
                for (var i = 1; i < loop.Length; i++)
                    ctx.LineTo(ToScreen(new Point(loop[i].X, loop[i].Y)));
                ctx.EndFigure(true);
            }
        }

        context.DrawGeometry(null, AntsUnderPen, geometry);
        context.DrawGeometry(null, AntsPen, geometry);
    }

    private IPen AntsPen => new Pen(
        Brushes.White, 1.1,
        new DashStyle([4, 4], _antsPhase));

    private void DrawSelectionDragPreview(DrawingContext context)
    {
        if (_isSelectDragging)
        {
            var rect = ScreenRect(_selectAnchor, _selectCurrent);

            if (Tool == CanvasTool.SelectEllipse)
            {
                var geometry = new EllipseGeometry(rect);
                context.DrawGeometry(null, PreviewUnderPen, geometry);
                context.DrawGeometry(null, PreviewPen, geometry);
            }
            else
            {
                context.DrawRectangle(null, PreviewUnderPen, rect);
                context.DrawRectangle(null, PreviewPen, rect);
            }

            return;
        }

        if (_lassoPoints is { Count: > 2 })
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(_lassoPoints[0], isFilled: false);
                for (var i = 1; i < _lassoPoints.Count; i++)
                    ctx.LineTo(_lassoPoints[i]);
                ctx.EndFigure(false);
            }

            context.DrawGeometry(null, PreviewUnderPen, geometry);
            context.DrawGeometry(null, PreviewPen, geometry);
        }
    }

    private Rect ScreenRect(Point a, Point b) => new(
        Math.Min(a.X, b.X),
        Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X),
        Math.Abs(a.Y - b.Y));

    private WriteableBitmap? GetTintBitmap(Selection selection)
    {
        if (ReferenceEquals(_tintSource, selection))
            return _tintBitmap;

        _tintBitmap?.Dispose();
        _tintBitmap = null;
        _tintSource = selection;

        var width = selection.Width;
        var height = selection.Height;
        if (width <= 0 || height <= 0 || (long)width * height > 24_000_000)
            return null;

        var bitmap = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);

        var buffer = new byte[width * height * 4];
        var coverage = selection.Coverage;

        // 选区着色：蓝紫色半透明，按覆盖度衰减
        const int maxAlpha = 62;
        for (var i = 0; i < coverage.Length; i++)
        {
            var c = coverage[i];
            if (c == 0)
                continue;

            var alpha = c * maxAlpha / 255;
            var offset = i * 4;
            buffer[offset] = (byte)(0xFF * alpha / 255);
            buffer[offset + 1] = (byte)(0x7A * alpha / 255);
            buffer[offset + 2] = (byte)(0x36 * alpha / 255);
            buffer[offset + 3] = (byte)alpha;
        }

        using (var frame = bitmap.Lock())
        {
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
                Marshal.Copy(buffer, y * rowBytes, frame.Address + y * frame.RowBytes, rowBytes);
        }

        _tintBitmap = bitmap;
        return bitmap;
    }

    private void DrawCropOverlay(DrawingContext context, Rect imageRect)
    {
        var crop = CropRect;
        if (crop.Width < 0.5 || crop.Height < 0.5)
            return;

        var rect = new Rect(
            imageRect.X + crop.X * Zoom,
            imageRect.Y + crop.Y * Zoom,
            crop.Width * Zoom,
            crop.Height * Zoom);

        // 框外压暗（只压图像范围内的区域）
        var top = new Rect(imageRect.X, imageRect.Y, imageRect.Width, Math.Max(0, rect.Y - imageRect.Y));
        var bottom = new Rect(imageRect.X, rect.Bottom, imageRect.Width, Math.Max(0, imageRect.Bottom - rect.Bottom));
        var left = new Rect(imageRect.X, rect.Y, Math.Max(0, rect.X - imageRect.X), rect.Height);
        var right = new Rect(rect.Right, rect.Y, Math.Max(0, imageRect.Right - rect.Right), rect.Height);
        context.FillRectangle(OutsideFill, top);
        context.FillRectangle(OutsideFill, bottom);
        context.FillRectangle(OutsideFill, left);
        context.FillRectangle(OutsideFill, right);

        // 三分线
        for (var i = 1; i <= 2; i++)
        {
            var x = rect.X + rect.Width * i / 3.0;
            var y = rect.Y + rect.Height * i / 3.0;
            context.DrawLine(CropThirdsPen, new Point(x, rect.Y), new Point(x, rect.Bottom));
            context.DrawLine(CropThirdsPen, new Point(rect.X, y), new Point(rect.Right, y));
        }

        context.DrawRectangle(null, CropPen, rect);

        // 四角把手
        const double half = 4.5;
        foreach (var corner in new[] { rect.TopLeft, rect.TopRight, rect.BottomLeft, rect.BottomRight })
        {
            var handle = new Rect(corner.X - half, corner.Y - half, half * 2, half * 2);
            context.FillRectangle(HandleFill, handle, 1);
            context.DrawRectangle(null, HandlePen, handle, 1);
        }
    }

    private static void DrawEmptyHint(DrawingContext context, Rect viewport)
    {
        var title = new FormattedText(
            "拖放图片到此处开始编辑",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            16,
            HintBrush);
        var subtitle = new FormattedText(
            "支持 PNG / JPEG / BMP / GIF / WEBP    ·    Ctrl+O 打开",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            Typeface.Default,
            12,
            HintBrush);

        var cx = viewport.Width / 2;
        var cy = viewport.Height / 2;
        context.DrawText(title, new Point(cx - title.Width / 2, cy - title.Height));
        context.DrawText(subtitle, new Point(cx - subtitle.Width / 2, cy + 12));
    }

    private static ImageBrush CheckerBrush
    {
        get
        {
            if (_checkerBrush is not null)
                return _checkerBrush;

            const int tile = 16;
            var bitmap = new WriteableBitmap(
                new PixelSize(tile, tile), new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);

            var buffer = new byte[tile * tile * 4];
            for (var y = 0; y < tile; y++)
            for (var x = 0; x < tile; x++)
            {
                var light = (x / 8 + y / 8) % 2 == 0;
                var value = (byte)(light ? 0x3A : 0x2C);
                var i = (y * tile + x) * 4;
                buffer[i] = value;
                buffer[i + 1] = value;
                buffer[i + 2] = value;
                buffer[i + 3] = 255;
            }

            using (var fb = bitmap.Lock())
                Marshal.Copy(buffer, 0, fb.Address, buffer.Length);

            _checkerBrush = new ImageBrush(bitmap)
            {
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill,
                SourceRect = new RelativeRect(0, 0, tile, tile, RelativeUnit.Absolute),
                DestinationRect = new RelativeRect(0, 0, tile, tile, RelativeUnit.Absolute)
            };
            return _checkerBrush;
        }
    }

    // ------------------------------------------------------------------ 交互

    public void SetZoomAt(double newZoom, Point anchor)
    {
        newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);
        var old = Zoom;
        if (old <= 0 || Math.Abs(old - newZoom) < 1e-9)
            return;

        var ratio = newZoom / old;
        Offset = new Point(
            anchor.X - (anchor.X - Offset.X) * ratio,
            anchor.Y - (anchor.Y - Offset.Y) * ratio);
        Zoom = newZoom;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateAntsTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _antsTimer.Stop();
        _tintBitmap?.Dispose();
        _tintBitmap = null;
        _tintSource = null;
    }

    private void UpdateAntsTimer()
    {
        var shouldRun = Selection is { IsEmpty: false } && ShowSelection;
        if (shouldRun && !_antsTimer.IsEnabled)
            _antsTimer.Start();
        else if (!shouldRun && _antsTimer.IsEnabled)
            _antsTimer.Stop();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ToolProperty)
        {
            Cursor = Tool switch
            {
                CanvasTool.Pan => MoveCursor,
                CanvasTool.Text => TextCursor,
                _ => CrossCursor
            };

            if (Tool != CanvasTool.Crop)
                _isCropping = false;

            // 切换工具时收尾未完成的笔画，避免状态泄漏
            if (_isPainting)
            {
                _isPainting = false;
                PaintEnded?.Invoke(this, EventArgs.Empty);
            }

            _isSelectDragging = false;
            _lassoPoints = null;
            _isGradientDragging = false;
            _hover = null;
            InvalidateVisual();
        }
        else if (change.Property == SelectionProperty || change.Property == ShowSelectionProperty)
        {
            UpdateAntsTimer();
            InvalidateVisual();
        }
    }

    private bool IsSelectionTool => Tool is CanvasTool.SelectRect or CanvasTool.SelectEllipse
        or CanvasTool.Lasso or CanvasTool.Wand;

    private bool IsPaintTool => Tool is CanvasTool.Brush or CanvasTool.Eraser;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetCurrentPoint(this);
        var position = point.Position;

        if (point.Properties.IsMiddleButtonPressed ||
            (point.Properties.IsLeftButtonPressed && Tool == CanvasTool.Pan))
        {
            _isPanning = true;
            _lastPointer = position;
        }
        else if (point.Properties.IsLeftButtonPressed)
        {
            BeginDrag(position, e.KeyModifiers);
        }

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <summary>开始一次拖动（裁剪 / 选区 / 套索 / 魔棒 / 画笔 / 渐变）。</summary>
    private bool BeginDrag(Point position, KeyModifiers modifiers)
    {
        if (Source is null)
            return false;

        var image = ClampToImage(ToImage(position));

        switch (Tool)
        {
            case CanvasTool.Crop:
                _cropAnchor = image;
                CropRect = new Rect(_cropAnchor, _cropAnchor);
                _isCropping = true;
                break;

            case CanvasTool.SelectRect:
            case CanvasTool.SelectEllipse:
                _selectAnchor = image;
                _selectCurrent = image;
                _isSelectDragging = true;
                break;

            case CanvasTool.Lasso:
                _lassoPoints = [ClampToScreen(position)];
                break;

            case CanvasTool.Wand:
                WandPickRequested?.Invoke(this, new WandPickEventArgs(ToImage(position), modifiers));
                break;

            case CanvasTool.Brush:
            case CanvasTool.Eraser:
                // 画笔不从画布边缘钳制，允许从外侧画入
                _hover = position;
                _isPainting = true;
                PaintStarted?.Invoke(this, new PaintEventArgs(ToImage(position)));
                break;

            case CanvasTool.Bucket:
                BucketPickRequested?.Invoke(this, new WandPickEventArgs(ToImage(position), modifiers));
                break;

            case CanvasTool.Gradient:
                _gradientStart = position;
                _gradientCurrent = position;
                _isGradientDragging = true;
                break;

            case CanvasTool.Text:
                TextRequested?.Invoke(this, new PaintEventArgs(ToImage(position)));
                break;

            default:
                return false;
        }

        InvalidateVisual();
        return true;
    }

    /// <summary>拖动过程中持续调用。</summary>
    private void UpdateDrag(Point position, KeyModifiers modifiers)
    {
        if (_isCropping)
        {
            var current = ClampToImage(ToImage(position));
            CropRect = new Rect(
                Math.Min(_cropAnchor.X, current.X),
                Math.Min(_cropAnchor.Y, current.Y),
                Math.Abs(current.X - _cropAnchor.X),
                Math.Abs(current.Y - _cropAnchor.Y));
        }
        else if (_isSelectDragging)
        {
            _selectCurrent = ClampToImage(ToImage(position));
            InvalidateVisual();
        }
        else if (_isPainting)
        {
            _hover = position;
            PaintMoved?.Invoke(this, new PaintEventArgs(ToImage(position)));
            InvalidateVisual();
        }
        else if (_isGradientDragging)
        {
            var current = position;

            // 按住 Shift 约束为水平 / 垂直，方便画规整的渐变
            if (modifiers.HasFlag(KeyModifiers.Shift))
            {
                var dx = current.X - _gradientStart.X;
                var dy = current.Y - _gradientStart.Y;
                current = Math.Abs(dx) >= Math.Abs(dy)
                    ? new Point(current.X, _gradientStart.Y)
                    : new Point(_gradientStart.X, current.Y);
            }

            _gradientCurrent = current;
            InvalidateVisual();
        }
        else if (_lassoPoints is not null)
        {
            var last = _lassoPoints[^1];
            var minDistance = Math.Max(0.75, 1.5 / Math.Max(Zoom, 0.05));
            if (Distance(last, ClampToScreen(position)) >= minDistance)
            {
                _lassoPoints.Add(ClampToScreen(position));
                InvalidateVisual();
            }
        }
    }

    /// <summary>结束拖动并抛出选区完成事件。</summary>
    private void EndDrag(KeyModifiers modifiers)
    {
        if (_isCropping)
        {
            _isCropping = false;
            return;
        }

        if (_isPainting)
        {
            _isPainting = false;
            PaintEnded?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_isGradientDragging)
        {
            _isGradientDragging = false;
            var start = ToImage(_gradientStart);
            var end = ToImage(_gradientCurrent);
            InvalidateVisual();

            if (Distance(_gradientStart, _gradientCurrent) >= 2)
                GradientCompleted?.Invoke(this, new GradientEventArgs(start, end));

            return;
        }

        if (_isSelectDragging)
        {
            _isSelectDragging = false;

            var imageRect = new Rect(
                Math.Min(_selectAnchor.X, _selectCurrent.X),
                Math.Min(_selectAnchor.Y, _selectCurrent.Y),
                Math.Abs(_selectCurrent.X - _selectAnchor.X),
                Math.Abs(_selectCurrent.Y - _selectAnchor.Y));

            InvalidateVisual();

            RectSelectionCompleted?.Invoke(this, new SelectionRectEventArgs(
                imageRect.Width >= 0.5 && imageRect.Height >= 0.5 ? imageRect : default,
                modifiers));
            return;
        }

        if (_lassoPoints is not { } points)
            return;

        _lassoPoints = null;
        InvalidateVisual();

        if (points.Count >= 3)
        {
            var imagePoints = points.Select(ToImage).ToArray();
            LassoSelectionCompleted?.Invoke(this, new SelectionPathEventArgs(imagePoints, modifiers));
        }
        else
        {
            LassoSelectionCompleted?.Invoke(this, new SelectionPathEventArgs([], modifiers));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var position = e.GetPosition(this);

        if (_isPanning)
        {
            Offset += position - _lastPointer;
            _lastPointer = position;
        }
        else
        {
            UpdateDrag(position, e.KeyModifiers);
        }

        if (IsPaintTool && !_isPainting)
        {
            _hover = position;
            InvalidateVisual();
        }

        var imagePoint = ToImage(position);
        var size = ImagePixelSize;
        var inside = imagePoint.X >= 0 && imagePoint.Y >= 0 &&
                     imagePoint.X < size.Width && imagePoint.Y < size.Height;

        PointerImageMoved?.Invoke(this, new CanvasPointerEventArgs(imagePoint, inside));
    }

    private Point ClampToScreen(Point screen)
    {
        var rect = ImageScreenRect;
        return new Point(
            Math.Clamp(screen.X, rect.X, rect.Right),
            Math.Clamp(screen.Y, rect.Y, rect.Bottom));
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (_isPanning)
        {
            _isPanning = false;
        }
        else
        {
            EndDrag(e.KeyModifiers);
        }

        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = null;
        InvalidateVisual();
        PointerImageMoved?.Invoke(this, new CanvasPointerEventArgs(default, false));
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Source is null)
            return;

        var anchor = e.GetPosition(this);
        var factor = Math.Pow(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1.4 : 1.15, e.Delta.Y);
        SetZoomAt(Zoom * factor, anchor);
        e.Handled = true;
    }
}
