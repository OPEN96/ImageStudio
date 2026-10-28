using System.Diagnostics;
using ImageStudio.Controls;
using ImageStudio.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Point = Avalonia.Point;

namespace ImageStudio.ViewModels;

/// <summary>
/// 移动工具：拖动当前图层的内容（有选区时只移动选区内的像素）。
/// 拖动过程复用两块像素缓冲做实时预览，松手时才真正写入图层并生成一条历史记录。
/// </summary>
public partial class MainViewModel
{
    private Layer? _moveLayer;
    private Image<Bgra32>? _movePreviewPixels;
    private Image<L8>? _movePreviewMask;
    private Bgra32[]? _movePixelSource;
    private Bgra32[]? _movePixelBuffer;
    private L8[]? _moveMaskSource;
    private L8[]? _moveMaskBuffer;
    private Point _moveOrigin;
    private int _moveDx;
    private int _moveDy;
    private int _moveIndex;
    private long _lastMoveTick;

    public bool IsMoveTool => ActiveTool == CanvasTool.Move;

    /// <summary>移动工具按下：记录起始点并把图层像素拷一份作为拖动的基准。</summary>
    public void OnMoveStart(Point position)
    {
        ReleaseMoveBuffers();

        if (_document is not { ActiveLayer: { } layer })
            return;

        if (layer.Width != _document.Width || layer.Height != _document.Height)
            return;

        var width = layer.Width;
        var height = layer.Height;
        var count = width * height;

        _moveLayer = layer;
        _moveIndex = _document.ActiveIndex;
        _moveOrigin = position;
        _moveDx = 0;
        _moveDy = 0;

        _movePixelSource = new Bgra32[count];
        layer.Pixels.CopyPixelDataTo(_movePixelSource);

        _movePixelBuffer = new Bgra32[count];
        _movePreviewPixels = Image.WrapMemory<Bgra32>(_movePixelBuffer.AsMemory(), width, height);

        if (layer.HasMask && layer.Mask is { } mask)
        {
            _moveMaskSource = new L8[count];
            mask.CopyPixelDataTo(_moveMaskSource);

            _moveMaskBuffer = new L8[count];
            _movePreviewMask = Image.WrapMemory<L8>(_moveMaskBuffer.AsMemory(), width, height);
        }
    }

    /// <summary>拖动中：按位移刷新预览（<paramref name="constrain"/> 为 Shift 时约束到单轴）。</summary>
    public void OnMoveUpdate(Point position, bool constrain)
    {
        if (_moveLayer is null)
            return;

        var dx = (int)Math.Round(position.X - _moveOrigin.X);
        var dy = (int)Math.Round(position.Y - _moveOrigin.Y);

        if (constrain)
        {
            if (Math.Abs(dx) >= Math.Abs(dy))
                dy = 0;
            else
                dx = 0;
        }

        if (dx == _moveDx && dy == _moveDy)
            return;

        _moveDx = dx;
        _moveDy = dy;
        PushMovePreview(force: false);
    }

    /// <summary>抬起：把位移写入图层（一条历史记录）。</summary>
    public void OnMoveEnd()
    {
        var layer = _moveLayer;
        var index = _moveIndex;
        var dx = _moveDx;
        var dy = _moveDy;

        ReleaseMoveBuffers();

        if (_document is null || layer is null)
            return;

        if (dx == 0 && dy == 0)
        {
            RefreshBitmap();
            return;
        }

        try
        {
            var moved = CommitMove(index, dx, dy, out var label);
            if (!moved)
            {
                RefreshBitmap();
                return;
            }

            StatusText = $"{label}：({dx:+#;-#;0}, {dy:+#;-#;0}) px";
        }
        catch (Exception ex)
        {
            StatusText = "移动失败：" + ex.Message;
            RefreshBitmap();
        }

        RefreshCommandStates();
    }

    /// <summary>方向键微调（Shift 为 10 像素）。</summary>
    public void NudgeActiveLayer(int dx, int dy)
    {
        if (_document is null || _isBusy || _document.ActiveLayer is null)
            return;

        try
        {
            if (CommitMove(_document.ActiveIndex, dx, dy, out var label))
                StatusText = $"{label}：({dx:+#;-#;0}, {dy:+#;-#;0}) px";
        }
        catch (Exception ex)
        {
            StatusText = "移动失败：" + ex.Message;
        }
    }

    private bool CommitMove(int index, int dx, int dy, out string label)
    {
        var clip = _selection is { IsEmpty: false } ? _selection : null;
        label = clip is null ? "移动图层" : "移动选区内容";
        return _document?.MoveLayerContent(index, dx, dy, clip, label) == true;
    }

    private void PushMovePreview(bool force)
    {
        if (_document is null || _moveLayer is null || _movePixelSource is null ||
            _movePixelBuffer is null || _movePreviewPixels is null)
            return;

        var now = Stopwatch.GetTimestamp();
        if (!force && (now - _lastMoveTick) * 1000.0 / Stopwatch.Frequency < PreviewIntervalMs)
            return;

        _lastMoveTick = now;

        var width = _moveLayer.Width;
        var height = _moveLayer.Height;
        var coverage = CoverageSpan(width, height);

        try
        {
            MoveOps.Translate(_movePixelSource, _movePixelBuffer, width, height, _moveDx, _moveDy, coverage);

            if (_moveMaskSource is not null && _moveMaskBuffer is not null)
                MoveOps.TranslateMask(_moveMaskSource, _moveMaskBuffer, width, height, _moveDx, _moveDy, coverage);

            using var composed = _document.CompositeWith(_moveIndex, _movePreviewPixels, _movePreviewMask);
            ShowPreviewBitmap(composed);
        }
        catch (Exception ex)
        {
            StatusText = "移动预览失败：" + ex.Message;
        }
    }

    private ReadOnlySpan<byte> CoverageSpan(int width, int height) =>
        _selection is { IsEmpty: false } selection &&
        selection.Width == width && selection.Height == height
            ? selection.Coverage
            : default;

    private void ReleaseMoveBuffers()
    {
        _moveLayer = null;
        _movePixelSource = null;
        _movePixelBuffer = null;
        _moveMaskSource = null;
        _moveMaskBuffer = null;

        _movePreviewPixels?.Dispose();
        _movePreviewPixels = null;

        _movePreviewMask?.Dispose();
        _movePreviewMask = null;
    }

    /// <summary>把合成结果刷进复用的预览位图（笔画与移动共用）。</summary>
    private void ShowPreviewBitmap(Image<Bgra32> composed)
    {
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
}
