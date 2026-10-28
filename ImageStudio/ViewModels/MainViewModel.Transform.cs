using System.Diagnostics;
using System.Numerics;
using Avalonia;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Controls;
using ImageStudio.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Point = Avalonia.Point;

namespace ImageStudio.ViewModels;

/// <summary>
/// 自由变换（Ctrl+T）：拖动变换框缩放 / 旋转 / 位移当前图层的内容，
/// 拖动过程中只刷新预览，确认后才写入图层并生成一条历史记录。
/// </summary>
public partial class MainViewModel
{
    private TransformGeometry _transform;
    private TransformGeometry _transformBaseline;
    private TransformGeometry _transformInitial;
    private TransformHandle _transformHandle;
    private Layer? _transformLayer;
    private int _transformIndex;
    private Point _transformDragOrigin;
    private long _lastTransformTick;
    private bool _syncingTransform;

    private Bgra32[]? _transformPixelSource;
    private Bgra32[]? _transformPixelBuffer;
    private L8[]? _transformMaskSource;
    private L8[]? _transformMaskBuffer;
    private Image<Bgra32>? _transformPreviewPixels;
    private Image<L8>? _transformPreviewMask;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CommitTransformCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelTransformCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResetTransformCommand))]
    public partial bool IsTransforming { get; set; }

    /// <summary>画布上显示的变换框（非空即表示正在变换）。</summary>
    [ObservableProperty]
    public partial TransformBox? TransformQuad { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransformAngleText))]
    public partial double TransformAngle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransformScaleXText))]
    public partial double TransformScaleXPercent { get; set; } = 100;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransformScaleYText))]
    public partial double TransformScaleYPercent { get; set; } = 100;

    [ObservableProperty]
    public partial bool TransformKeepAspect { get; set; } = true;

    public string TransformAngleText => $"{TransformAngle:0.#}°";

    public string TransformScaleXText => $"{TransformScaleXPercent:0.#}%";

    public string TransformScaleYText => $"{TransformScaleYPercent:0.#}%";

    public string TransformHint =>
        "拖角点缩放（Shift 等比 · Alt 中心）· 角外旋转（Shift 15°）· 框内移动";

    // ------------------------------------------------------------------ 命令

    [RelayCommand]
    private void BeginTransform() => StartTransform();

    [RelayCommand(CanExecute = nameof(IsTransforming))]
    private void CommitTransform() => ApplyTransform();

    [RelayCommand(CanExecute = nameof(IsTransforming))]
    private void CancelTransform()
    {
        if (!IsTransforming)
            return;

        EndTransformSession();
        RefreshBitmap();
        StatusText = "已取消自由变换";
    }

    [RelayCommand(CanExecute = nameof(IsTransforming))]
    private void ResetTransform()
    {
        if (!IsTransforming)
            return;

        ApplyGeometry(_transformInitial);
        StatusText = "已重置变换参数";
    }

    // ------------------------------------------------------------------ 会话

    public void StartTransform()
    {
        if (IsTransforming)
        {
            StatusText = "已经在自由变换中：Enter 应用，Esc 取消";
            return;
        }

        if (_document is not { ActiveLayer: { } layer } || _isBusy)
            return;

        if (layer.Width != _document.Width || layer.Height != _document.Height)
        {
            StatusText = "图层尺寸与画布不一致，无法变换";
            return;
        }

        var clip = _selection is { IsEmpty: false } selection &&
                   selection.Width == layer.Width && selection.Height == layer.Height
            ? selection
            : null;

        // 有选区时只变换选区内容，否则按不透明内容的包围盒给框
        var bounds = clip?.Bounds ?? TransformOps.ContentBounds(layer.Pixels);

        _transformIndex = _document.ActiveIndex;
        _transformInitial = TransformGeometry.ForBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        _transform = _transformInitial;
        _transformBaseline = _transformInitial;

        // 注意顺序：分配缓冲会先释放上一次的会话状态（含 _transformLayer）
        AllocateTransformBuffers(layer);
        _transformLayer = layer;

        IsTransforming = true;
        SyncTransformProperties();
        UpdateTransformQuad();
        PushTransformPreview(force: true);

        StatusText = clip is null
            ? "自由变换整个图层：拖角点缩放 · 角外旋转 · 框内移动 · Enter 应用 · Esc 取消"
            : "自由变换选区内容：拖角点缩放 · 角外旋转 · 框内移动 · Enter 应用 · Esc 取消";
    }

    private void AllocateTransformBuffers(Layer layer)
    {
        ReleaseTransformBuffers();

        var width = layer.Width;
        var height = layer.Height;
        var count = width * height;

        _transformPixelSource = new Bgra32[count];
        layer.Pixels.CopyPixelDataTo(_transformPixelSource);

        _transformPixelBuffer = new Bgra32[count];
        _transformPreviewPixels = Image.WrapMemory<Bgra32>(_transformPixelBuffer.AsMemory(), width, height);

        if (layer.HasMask && layer.Mask is { } mask)
        {
            _transformMaskSource = new L8[count];
            mask.CopyPixelDataTo(_transformMaskSource);

            _transformMaskBuffer = new L8[count];
            _transformPreviewMask = Image.WrapMemory<L8>(_transformMaskBuffer.AsMemory(), width, height);
        }
    }

    // ------------------------------------------------------------------ 画布交互

    /// <summary>开始拖动某个操作点：记下起始位置与基准参数。</summary>
    public void OnTransformDragStart(TransformHandle handle, Point position)
    {
        if (!IsTransforming)
            return;

        _transformHandle = handle;
        _transformDragOrigin = position;
        _transformBaseline = _transform;
    }

    /// <summary>拖动中：从基准状态重算，避免误差累积。</summary>
    public void OnTransformDragMove(Point position, KeyModifiers modifiers)
    {
        if (!IsTransforming)
            return;

        var pointer = new Vector2((float)position.X, (float)position.Y);
        var origin = new Vector2((float)_transformDragOrigin.X, (float)_transformDragOrigin.Y);
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);

        var geometry = _transformHandle switch
        {
            TransformHandle.None => _transform,
            TransformHandle.Move => TransformMath.Translate(_transformBaseline, pointer - origin),
            TransformHandle.RotateTopLeft or TransformHandle.RotateTopRight
                or TransformHandle.RotateBottomRight or TransformHandle.RotateBottomLeft =>
                TransformMath.RotateTo(_transformBaseline, origin, pointer, shift),
            _ => TransformMath.Resize(_transformBaseline, _transformHandle, pointer, shift, alt)
        };

        ApplyGeometry(geometry, resetBaseline: false);
    }

    public void OnTransformDragEnd()
    {
        if (!IsTransforming)
            return;

        _transformHandle = TransformHandle.None;
        _transformBaseline = _transform;
        PushTransformPreview(force: true);
    }

    // ------------------------------------------------------------------ 参数面板

    partial void OnTransformAngleChanged(double value)
    {
        if (_syncingTransform || !IsTransforming)
            return;

        var geometry = _transform;
        geometry.Rotation = value * Math.PI / 180;
        ApplyGeometry(geometry);
    }

    partial void OnTransformScaleXPercentChanged(double value)
    {
        if (_syncingTransform || !IsTransforming)
            return;

        var geometry = _transform;
        var previous = geometry.ScaleX;
        if (Math.Abs(previous) < 1e-6)
            return;

        geometry.ScaleX = (previous < 0 ? -1 : 1) * Math.Max(value, 1) / 100;

        if (TransformKeepAspect)
            geometry.ScaleY *= geometry.ScaleX / previous;

        ApplyGeometry(geometry);
    }

    partial void OnTransformScaleYPercentChanged(double value)
    {
        if (_syncingTransform || !IsTransforming)
            return;

        var geometry = _transform;
        var previous = geometry.ScaleY;
        if (Math.Abs(previous) < 1e-6)
            return;

        geometry.ScaleY = (previous < 0 ? -1 : 1) * Math.Max(value, 1) / 100;

        if (TransformKeepAspect)
            geometry.ScaleX *= geometry.ScaleY / previous;

        ApplyGeometry(geometry);
    }

    private void ApplyGeometry(in TransformGeometry geometry, bool resetBaseline = true)
    {
        _transform = geometry;

        if (resetBaseline)
            _transformBaseline = geometry;

        SyncTransformProperties();
        UpdateTransformQuad();
        PushTransformPreview(force: false);
    }

    private void SyncTransformProperties()
    {
        _syncingTransform = true;
        TransformAngle = Math.Round(NormalizeDegrees(_transform.Rotation * 180 / Math.PI), 1);
        TransformScaleXPercent = Math.Round(Math.Abs(_transform.ScaleX) * 100, 1);
        TransformScaleYPercent = Math.Round(Math.Abs(_transform.ScaleY) * 100, 1);
        _syncingTransform = false;
    }

    private static double NormalizeDegrees(double degrees)
    {
        var value = (degrees + 180) % 360;
        if (value < 0)
            value += 360;
        return value - 180;
    }

    private void UpdateTransformQuad()
    {
        var corners = _transform.Corners();
        TransformQuad = new TransformBox(
            new Point(corners[0].X, corners[0].Y),
            new Point(corners[1].X, corners[1].Y),
            new Point(corners[2].X, corners[2].Y),
            new Point(corners[3].X, corners[3].Y));
    }

    // ------------------------------------------------------------------ 预览与提交

    private void PushTransformPreview(bool force)
    {
        if (_document is null || _transformLayer is null || _transformPixelSource is null ||
            _transformPixelBuffer is null || _transformPreviewPixels is null)
            return;

        var now = Stopwatch.GetTimestamp();
        if (!force && (now - _lastTransformTick) * 1000.0 / Stopwatch.Frequency < PreviewIntervalMs)
            return;

        _lastTransformTick = now;

        var width = _transformLayer.Width;
        var height = _transformLayer.Height;
        var coverage = CoverageSpan(width, height);

        try
        {
            TransformOps.Warp(_transformPixelSource, _transformPixelBuffer, width, height, _transform, coverage);

            if (_transformMaskSource is not null && _transformMaskBuffer is not null)
                TransformOps.WarpMask(_transformMaskSource, _transformMaskBuffer, width, height, _transform, coverage);

            using var composed = _document.CompositeWith(_transformIndex, _transformPreviewPixels, _transformPreviewMask);
            ShowPreviewBitmap(composed);
        }
        catch (Exception ex)
        {
            StatusText = "变换预览失败：" + ex.Message;
        }
    }

    private void ApplyTransform()
    {
        if (!IsTransforming || _document is null)
            return;

        var geometry = _transform;
        var index = _transformIndex;
        var clip = CoverageSelection();

        EndTransformSession();

        if (geometry.IsIdentity)
        {
            RefreshBitmap();
            StatusText = "变换已结束（内容没有变化）";
            RefreshCommandStates();
            return;
        }

        try
        {
            if (_document.TransformLayerContent(index, geometry, clip, "自由变换"))
                StatusText = "自由变换：" + DescribeTransform(geometry);
        }
        catch (Exception ex)
        {
            StatusText = "变换失败：" + ex.Message;
            RefreshBitmap();
        }

        RefreshCommandStates();
    }

    private Selection? CoverageSelection()
    {
        if (_document is null)
            return null;

        return _selection is { IsEmpty: false } selection &&
               selection.Width == _document.Width && selection.Height == _document.Height
            ? selection
            : null;
    }

    private static string DescribeTransform(in TransformGeometry geometry)
    {
        var parts = new List<string>();

        var scaleX = Math.Abs(geometry.ScaleX) * 100;
        var scaleY = Math.Abs(geometry.ScaleY) * 100;
        if (Math.Abs(scaleX - 100) > 0.5 || Math.Abs(scaleY - 100) > 0.5)
            parts.Add($"缩放 {scaleX:0.#}% × {scaleY:0.#}%");

        var degrees = geometry.Rotation * 180 / Math.PI;
        if (Math.Abs(degrees) > 0.1)
            parts.Add($"旋转 {degrees:0.#}°");

        var dx = geometry.CenterX - geometry.PivotX;
        var dy = geometry.CenterY - geometry.PivotY;
        if (Math.Abs(dx) > 0.5 || Math.Abs(dy) > 0.5)
            parts.Add($"位移 ({dx:+0.#;-0.#;0}, {dy:+0.#;-0.#;0})");

        return parts.Count == 0 ? "无变化" : string.Join(" · ", parts);
    }

    private void EndTransformSession()
    {
        IsTransforming = false;
        _transformHandle = TransformHandle.None;
        TransformQuad = null;
        ReleaseTransformBuffers();
    }

    private void ReleaseTransformBuffers()
    {
        _transformLayer = null;
        _transformPixelSource = null;
        _transformPixelBuffer = null;
        _transformMaskSource = null;
        _transformMaskBuffer = null;

        _transformPreviewPixels?.Dispose();
        _transformPreviewPixels = null;

        _transformPreviewMask?.Dispose();
        _transformPreviewMask = null;
    }
}
