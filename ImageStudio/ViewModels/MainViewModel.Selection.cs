using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Controls;
using ImageStudio.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// Avalonia 与 ImageSharp 存在同名类型，这里显式指定使用 Avalonia 的几何/颜色类型。
using Point = Avalonia.Point;
using Color = Avalonia.Media.Color;

namespace ImageStudio.ViewModels;

/// <summary>选区相关的状态与命令。</summary>
public partial class MainViewModel
{
    private Selection? _selection;
    private bool _syncingColor;

    // ------------------------------------------------------------------ 工具

    [ObservableProperty]
    public partial CanvasTool ActiveTool { get; set; } = CanvasTool.Pan;

    public bool IsPanTool => ActiveTool == CanvasTool.Pan;

    public bool IsCropTool => ActiveTool == CanvasTool.Crop;

    public bool IsRectSelectTool => ActiveTool == CanvasTool.SelectRect;

    public bool IsEllipseSelectTool => ActiveTool == CanvasTool.SelectEllipse;

    public bool IsLassoTool => ActiveTool == CanvasTool.Lasso;

    public bool IsWandTool => ActiveTool == CanvasTool.Wand;

    /// <summary>当前是否处于任一选区工具下。</summary>
    public bool IsSelectionToolActive => ActiveTool is CanvasTool.SelectRect
        or CanvasTool.SelectEllipse or CanvasTool.Lasso or CanvasTool.Wand;

    /// <summary>兼容原有裁剪面板的绑定。</summary>
    public bool IsCropMode => ActiveTool == CanvasTool.Crop;

    public string ActiveToolHint => ActiveTool switch
    {
        CanvasTool.Crop => "拖动框选区域，然后点“应用裁剪”",
        CanvasTool.SelectRect => "拖动框选 · Shift 添加 · Alt 减去 · 单击取消",
        CanvasTool.SelectEllipse => "拖动框选 · Shift 添加 · Alt 减去 · 单击取消",
        CanvasTool.Lasso => "按住左键描绘轮廓，松开自动闭合",
        CanvasTool.Wand => "点击选取相近颜色，可调整容差",
        _ => "按住左键拖动画布，滚轮缩放"
    };

    partial void OnActiveToolChanged(CanvasTool value) => NotifyToolState();

    private void NotifyToolState()
    {
        OnPropertyChanged(nameof(IsPanTool));
        OnPropertyChanged(nameof(IsCropTool));
        OnPropertyChanged(nameof(IsRectSelectTool));
        OnPropertyChanged(nameof(IsEllipseSelectTool));
        OnPropertyChanged(nameof(IsLassoTool));
        OnPropertyChanged(nameof(IsWandTool));
        OnPropertyChanged(nameof(IsSelectionToolActive));
        OnPropertyChanged(nameof(IsCropMode));
        OnPropertyChanged(nameof(ActiveToolHint));
        OnPropertyChanged(nameof(CropText));
    }

    public void SetTool(CanvasTool tool)
    {
        ActiveTool = tool;

        // 重复点击同一个工具时属性不会变更，这里强制同步一次单选按钮状态
        NotifyToolState();
        StatusText = ActiveToolHint;
    }

    // ------------------------------------------------------------------ 选区状态

    public Selection? CurrentSelection => _selection;

    public bool HasSelection => _selection is { IsEmpty: false };

    /// <summary>状态栏用的选区描述（无选区时为空态提示）。</summary>
    public string SelectionText => _selection is { IsEmpty: false } sel
        ? $"{sel.Bounds.Width} × {sel.Bounds.Height} px"
        : "无选区";

    [ObservableProperty]
    public partial SelectionCombine SelectionMode { get; set; } = SelectionCombine.Replace;

    [ObservableProperty]
    public partial double WandTolerance { get; set; } = 32;

    public double WandToleranceMax => 255;

    [ObservableProperty]
    public partial bool WandContiguous { get; set; } = true;

    [ObservableProperty]
    public partial double FeatherRadius { get; set; } = 8;

    public bool IsModeReplace => SelectionMode == SelectionCombine.Replace;

    public bool IsModeAdd => SelectionMode == SelectionCombine.Add;

    public bool IsModeSubtract => SelectionMode == SelectionCombine.Subtract;

    public bool IsModeIntersect => SelectionMode == SelectionCombine.Intersect;

    partial void OnSelectionModeChanged(SelectionCombine value)
    {
        OnPropertyChanged(nameof(IsModeReplace));
        OnPropertyChanged(nameof(IsModeAdd));
        OnPropertyChanged(nameof(IsModeSubtract));
        OnPropertyChanged(nameof(IsModeIntersect));
        NotifySelectionCommands();
    }

    public void SetSelectionMode(SelectionCombine mode)
    {
        SelectionMode = mode;
        StatusText = mode switch
        {
            SelectionCombine.Add => "选区模式：添加到选区",
            SelectionCombine.Subtract => "选区模式：从选区减去",
            SelectionCombine.Intersect => "选区模式：与选区交叉",
            _ => "选区模式：新建选区"
        };
    }

    // ------------------------------------------------------------------ 前景色

    [ObservableProperty]
    public partial Color FillColor { get; set; } = Color.FromRgb(0x4F, 0x8C, 0xFF);

    [ObservableProperty]
    public partial string FillHex { get; set; } = "4F8CFF";

    public IBrush FillBrush => new SolidColorBrush(FillColor);

    partial void OnFillColorChanged(Color value)
    {
        OnPropertyChanged(nameof(FillBrush));
    }

    partial void OnFillHexChanged(string value)
    {
        if (_syncingColor)
            return;

        var text = value.Trim().TrimStart('#');
        if (text.Length == 3)
            text = string.Concat(text.Select(c => new string(c, 2)));

        if (text.Length != 6 || !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return;

        _syncingColor = true;
        FillColor = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        _syncingColor = false;
    }

    public void SetFillColor(Color color)
    {
        _syncingColor = true;
        FillColor = color;
        FillHex = $"{color.R:X2}{color.G:X2}{color.B:X2}";
        _syncingColor = false;
        StatusText = $"前景色：#{FillHex}";
    }

    // ------------------------------------------------------------------ 选区变更

    private void SetSelection(Selection? next)
    {
        if (ReferenceEquals(_selection, next))
            return;

        _selection = next;
        OnPropertyChanged(nameof(CurrentSelection));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        NotifySelectionCommands();
    }

    /// <summary>把新选区按模式合并进当前选区。</summary>
    private void ApplySelection(Selection next, SelectionCombine mode, string label)
    {
        if (_document is null)
            return;

        if (mode != SelectionCombine.Replace && _selection is null)
        {
            StatusText = "当前没有选区，无法使用该组合模式";
            return;
        }

        var result = _selection is null ? next : _selection.Combine(next, mode);

        if (result.IsEmpty)
        {
            SetSelection(null);
            StatusText = label + "：选区为空";
            return;
        }

        SetSelection(result);
        StatusText = $"{label} · {SelectionText}";
    }

    private SelectionCombine ModeWithModifiers(KeyModifiers modifiers)
    {
        var shift = modifiers.HasFlag(KeyModifiers.Shift);
        var alt = modifiers.HasFlag(KeyModifiers.Alt);

        if (shift && alt)
            return SelectionCombine.Intersect;
        if (shift)
            return SelectionCombine.Add;
        if (alt)
            return SelectionCombine.Subtract;

        return SelectionMode;
    }

    // ------------------------------------------------------------------ 画布回调

    public async Task OnRectSelectionAsync(Rect rect, KeyModifiers modifiers)
    {
        if (_document is null || rect.Width < 0.5 || rect.Height < 0.5)
        {
            // 单击（没有拖动）取消选区，与 Photoshop 一致
            if (HasSelection)
            {
                SetSelection(null);
                StatusText = "已取消选区";
            }

            return;
        }

        var mode = ModeWithModifiers(modifiers);
        var ellipse = ActiveTool == CanvasTool.SelectEllipse;
        var area = new Rectangle(
            (int)Math.Round(rect.X), (int)Math.Round(rect.Y),
            (int)Math.Round(rect.Width), (int)Math.Round(rect.Height));

        var width = _document.Width;
        var height = _document.Height;

        var next = await Task.Run(() => ellipse
            ? Selection.FromEllipse(width, height, area)
            : Selection.FromRect(width, height, area));

        ApplySelection(next, mode, ellipse ? "椭圆选区" : "矩形选区");
    }

    public async Task OnLassoSelectionAsync(IReadOnlyList<Point> points, KeyModifiers modifiers)
    {
        if (_document is null || points.Count < 3)
            return;

        var mode = ModeWithModifiers(modifiers);
        var width = _document.Width;
        var height = _document.Height;
        var polygon = points.Select(p => ((float)p.X, (float)p.Y)).ToList();

        var next = await Task.Run(() => Selection.FromPolygon(width, height, polygon));
        ApplySelection(next, mode, "套索选区");
    }

    public async Task OnWandPickAsync(Point position, KeyModifiers modifiers)
    {
        if (_document is null)
            return;

        var x = (int)Math.Floor(position.X);
        var y = (int)Math.Floor(position.Y);
        if (x < 0 || y < 0 || x >= _document.Width || y >= _document.Height)
            return;

        var mode = ModeWithModifiers(modifiers);
        var tolerance = WandTolerance;
        var contiguous = WandContiguous;

        try
        {
            IsBusy = true;
            var source = _document.Composite().Clone();

            var next = await Task.Run(() =>
            {
                using (source)
                    return Selection.Wand(source, x, y, tolerance, contiguous);
            });

            ApplySelection(next, mode, $"魔棒（容差 {tolerance:0}）");
        }
        catch (Exception ex)
        {
            StatusText = "魔棒失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ 命令

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void SelectAll()
    {
        if (_document is null)
            return;

        SetSelection(Selection.Full(_document.Width, _document.Height));
        StatusText = $"已全选 · {SelectionText}";
    }

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private void Deselect()
    {
        SetSelection(null);
        StatusText = "已取消选区";
    }

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private async Task InvertSelectionAsync()
    {
        if (_selection is not { } selection)
            return;

        var next = await Task.Run(selection.Invert);
        SetSelection(next);
        StatusText = $"已反选 · {SelectionText}";
    }

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private async Task FeatherSelectionAsync()
    {
        if (_selection is not { } selection)
            return;

        var radius = FeatherRadius;
        try
        {
            IsBusy = true;
            var next = await Task.Run(() => selection.Feather(radius));
            SetSelection(next);
            StatusText = $"选区已羽化 {radius:0.#} px · {SelectionText}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCropToSelection))]
    private async Task CropToSelectionAsync()
    {
        if (_document is null || _selection is null)
            return;

        var bounds = _selection.Bounds;
        if (bounds.Width < 1 || bounds.Height < 1)
            return;

        SetSelection(null);
        await CommitDocumentAsync($"裁剪到选区 {bounds.Width}×{bounds.Height}",
            img => ImageOps.Crop(img, bounds),
            mask => ImageOps.Crop(mask, bounds));
    }

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private async Task LayerViaCopyAsync()
    {
        if (_document?.ActiveLayer is not { } layer || _selection is not { } selection)
            return;

        try
        {
            IsBusy = true;
            var pixels = await Task.Run(() => ImageOps.ExtractByMask(layer.Pixels, selection));
            var name = $"拷贝 {_document.LayerCount}";
            _document.AddLayerFromPixels(name, pixels);
            StatusText = $"已通过拷贝建立图层「{name}」";
        }
        catch (Exception ex)
        {
            StatusText = "建立图层失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private async Task DeleteSelectionContentAsync()
    {
        if (_document?.ActiveLayer is not { } layer || _selection is not { } selection || _isBusy)
            return;

        try
        {
            _isBusy = true;
            IsBusy = true;

            using (_document.BeginEdit("删除选区内容"))
            {
                var result = await Task.Run(() => ImageOps.ClearByMask(layer.Pixels, selection));
                layer.ReplacePixels(result);
            }

            StatusText = "已删除选区内容";
        }
        catch (Exception ex)
        {
            StatusText = "删除失败：" + ex.Message;
        }
        finally
        {
            _isBusy = false;
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private async Task FillSelectionAsync()
    {
        if (_document?.ActiveLayer is not { } layer || _selection is not { } selection || _isBusy)
            return;

        var color = FillColor;
        var fill = new Bgra32 { R = color.R, G = color.G, B = color.B, A = 255 };

        try
        {
            _isBusy = true;
            IsBusy = true;

            using (_document.BeginEdit("填充选区"))
            {
                var result = await Task.Run(() => ImageOps.FillByMask(layer.Pixels, selection, fill));
                layer.ReplacePixels(result);
            }

            StatusText = $"已用 #{FillHex} 填充选区";
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

    [RelayCommand(CanExecute = nameof(CanUseSelection))]
    private void SelectionToMask()
    {
        if (_document is null || _selection is not { } selection)
            return;

        _document.SetMaskFromSelection(_document.ActiveIndex, selection);
        StatusText = "已由选区生成图层蒙版";
    }

    private bool CanUseSelection() => HasSelection;

    private bool CanCropToSelection() => HasSelection;

    private void NotifySelectionCommands()
    {
        DeselectCommand.NotifyCanExecuteChanged();
        InvertSelectionCommand.NotifyCanExecuteChanged();
        FeatherSelectionCommand.NotifyCanExecuteChanged();
        CropToSelectionCommand.NotifyCanExecuteChanged();
        LayerViaCopyCommand.NotifyCanExecuteChanged();
        DeleteSelectionContentCommand.NotifyCanExecuteChanged();
        FillSelectionCommand.NotifyCanExecuteChanged();
        SelectionToMaskCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
    }
}
