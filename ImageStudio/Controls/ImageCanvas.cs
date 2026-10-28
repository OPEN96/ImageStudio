using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ImageStudio.Controls;

public sealed class CanvasPointerEventArgs(Point imagePosition, bool isInside) : EventArgs
{
    public Point ImagePosition { get; } = imagePosition;
    public bool IsInside { get; } = isInside;
}

/// <summary>
/// 图像显示区：负责棋盘格背景、缩放、平移、裁剪框交互。
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

    public static readonly StyledProperty<bool> IsCropModeProperty =
        AvaloniaProperty.Register<ImageCanvas, bool>(nameof(IsCropMode));

    public static readonly StyledProperty<Rect> CropRectProperty =
        AvaloniaProperty.Register<ImageCanvas, Rect>(
            nameof(CropRect), default, defaultBindingMode: BindingMode.TwoWay);

    private static readonly IBrush CanvasBackground = new SolidColorBrush(Color.Parse("#151519"));
    private static readonly IPen ImageBorderPen = new Pen(new SolidColorBrush(Color.Parse("#3D3D47")), 1);
    private static readonly IPen CropPen = new Pen(Brushes.White, 1.2);
    private static readonly IPen CropThirdsPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1);
    private static readonly IBrush OutsideFill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
    private static readonly IBrush HandleFill = Brushes.White;
    private static readonly IPen HandlePen = new Pen(new SolidColorBrush(Color.Parse("#111114")), 1);
    private static readonly IBrush HintBrush = new SolidColorBrush(Color.Parse("#6E6E7A"));

    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);

    private static ImageBrush? _checkerBrush;

    private bool _isPanning;
    private bool _isCropping;
    private Point _lastPointer;
    private Point _cropAnchor;

    static ImageCanvas()
    {
        AffectsRender<ImageCanvas>(
            SourceProperty, ZoomProperty, OffsetProperty,
            ShowCheckerboardProperty, IsCropModeProperty, CropRectProperty);
        FocusableProperty.OverrideDefaultValue<ImageCanvas>(true);
        ClipToBoundsProperty.OverrideDefaultValue<ImageCanvas>(true);
    }

    public ImageCanvas()
    {
        Cursor = MoveCursor;
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

    public bool IsCropMode
    {
        get => GetValue(IsCropModeProperty);
        set => SetValue(IsCropModeProperty, value);
    }

    public Rect CropRect
    {
        get => GetValue(CropRectProperty);
        set => SetValue(CropRectProperty, value);
    }

    public event EventHandler<CanvasPointerEventArgs>? PointerImageMoved;

    // ------------------------------------------------------------------ 尺寸换算

    public Size ImagePixelSize => Source is { } bmp
        ? new Size(bmp.PixelSize.Width, bmp.PixelSize.Height)
        : default;

    public Rect ImageScreenRect => new(Offset, new Size(ImagePixelSize.Width * Zoom, ImagePixelSize.Height * Zoom));

    /// <summary>控件坐标 → 图像像素坐标。</summary>
    public Point ToImage(Point screen) => new(
        Zoom <= 0 ? 0 : (screen.X - Offset.X) / Zoom,
        Zoom <= 0 ? 0 : (screen.Y - Offset.Y) / Zoom);

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

        if (IsCropMode)
            DrawCropOverlay(context, dest);
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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsCropModeProperty)
            Cursor = IsCropMode ? CrossCursor : MoveCursor;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        var point = e.GetCurrentPoint(this);
        var position = point.Position;

        if (point.Properties.IsMiddleButtonPressed ||
            (point.Properties.IsLeftButtonPressed && !IsCropMode))
        {
            _isPanning = true;
            _lastPointer = position;
            Cursor = MoveCursor;
        }
        else if (point.Properties.IsLeftButtonPressed && IsCropMode && Source is not null)
        {
            _cropAnchor = ClampToImage(ToImage(position));
            CropRect = new Rect(_cropAnchor, _cropAnchor);
            _isCropping = true;
        }

        e.Pointer.Capture(this);
        e.Handled = true;
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
        else if (_isCropping && Source is not null)
        {
            var current = ClampToImage(ToImage(position));
            CropRect = new Rect(
                Math.Min(_cropAnchor.X, current.X),
                Math.Min(_cropAnchor.Y, current.Y),
                Math.Abs(current.X - _cropAnchor.X),
                Math.Abs(current.Y - _cropAnchor.Y));
        }

        var imagePoint = ToImage(position);
        var size = ImagePixelSize;
        var inside = imagePoint.X >= 0 && imagePoint.Y >= 0 &&
                     imagePoint.X < size.Width && imagePoint.Y < size.Height;

        PointerImageMoved?.Invoke(this, new CanvasPointerEventArgs(imagePoint, inside));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _isPanning = false;
        _isCropping = false;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
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
