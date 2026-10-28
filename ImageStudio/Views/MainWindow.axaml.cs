using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using ImageStudio.Controls;
using ImageStudio.Models;
using ImageStudio.ViewModels;

namespace ImageStudio.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        Canvas.SizeChanged += (_, e) => _viewModel.SetViewport(e.NewSize);
        Canvas.PointerImageMoved += (_, e) => _viewModel.OnCanvasPointer(e.ImagePosition, e.IsInside);

        Canvas.RectSelectionCompleted += async (_, e) => await GuardedAsync(
            () => _viewModel.OnRectSelectionAsync(e.Rect, e.Modifiers));

        Canvas.LassoSelectionCompleted += async (_, e) => await GuardedAsync(
            () => _viewModel.OnLassoSelectionAsync(e.Points, e.Modifiers));

        Canvas.WandPickRequested += async (_, e) => await GuardedAsync(
            () => _viewModel.OnWandPickAsync(e.Position, e.Modifiers));

        Canvas.PaintStarted += (_, e) => _viewModel.OnPaintStart(e.Position);
        Canvas.PaintMoved += (_, e) => _viewModel.OnPaintMove(e.Position);
        Canvas.PaintEnded += (_, _) => _viewModel.OnPaintEnd();

        Canvas.BucketPickRequested += async (_, e) => await GuardedAsync(
            () => _viewModel.OnBucketAsync(e.Position));

        Canvas.GradientCompleted += async (_, e) => await GuardedAsync(
            () => _viewModel.OnGradientAsync(e.Start, e.End));

        Canvas.TextRequested += async (_, e) => await GuardedAsync(
            () => _viewModel.OnTextAsync(e.Position));

        Canvas.MoveStarted += (_, e) => _viewModel.OnMoveStart(e.Position);
        Canvas.MoveMoved += (_, e) =>
            _viewModel.OnMoveUpdate(e.Position, e.Modifiers.HasFlag(KeyModifiers.Shift));
        Canvas.MoveEnded += (_, _) => _viewModel.OnMoveEnd();

        Canvas.TransformDragStarted += (_, e) => _viewModel.OnTransformDragStart(e.Handle, e.Position);
        Canvas.TransformDragMoved += (_, e) => _viewModel.OnTransformDragMove(e.Position, e.Modifiers);
        Canvas.TransformDragEnded += (_, _) => _viewModel.OnTransformDragEnd();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private async Task GuardedAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _viewModel.StatusHint("操作失败：" + ex.Message);
        }
    }

    /// <summary>启动时打开指定图片。</summary>
    public void OpenPath(string path)
    {
        Opened += async (_, _) => await _viewModel.OpenPathAsync(path);
    }

    private bool _closeRequested;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // 有未保存修改时先拦下来问一次，确认后再真正关闭
        if (_closeRequested || !_viewModel.IsDirty)
        {
            _viewModel.Dispose();
            base.OnClosing(e);
            return;
        }

        // 未应用的变换不应参与保存流程
        _viewModel.CancelTransformCommand.Execute(null);

        e.Cancel = true;
        base.OnClosing(e);
        _ = ConfirmCloseAsync();
    }

    private async Task ConfirmCloseAsync()
    {
        bool canClose;
        try
        {
            canClose = await _viewModel.ConfirmCloseAsync();
        }
        catch (Exception ex)
        {
            _viewModel.StatusHint("关闭失败：" + ex.Message);
            return;
        }

        if (!canClose)
            return;

        _closeRequested = true;
        Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _viewModel.Storage = StorageProvider;
        _viewModel.Owner = this;
        _viewModel.SetViewport(Canvas.Bounds.Size);
    }

    // ------------------------------------------------------------------ 工具

    private void OnMoveToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetTool(CanvasTool.Move);

    private void OnPanToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetTool(CanvasTool.Pan);

    /// <summary>自由变换按钮：未进入时开始，已在变换中则当作“应用”。</summary>
    private void OnTransformToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.IsTransforming)
            _viewModel.CommitTransformCommand.Execute(null);
        else
            _viewModel.BeginTransformCommand.Execute(null);
    }

    private void OnCropToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_viewModel.IsCropTool)
            _viewModel.SetTool(CanvasTool.Pan);
        else
            _viewModel.BeginCropCommand.Execute(null);
    }

    private void OnRectSelectToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetTool(CanvasTool.SelectRect);

    private void OnEllipseSelectToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetTool(CanvasTool.SelectEllipse);

    private void OnLassoToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetTool(CanvasTool.Lasso);

    private void OnWandToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetTool(CanvasTool.Wand);

    private void OnBrushToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetTool(CanvasTool.Brush);

    private void OnEraserToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetTool(CanvasTool.Eraser);

    private void OnBucketToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetTool(CanvasTool.Bucket);

    private void OnGradientToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetTool(CanvasTool.Gradient);

    private void OnTextToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetTool(CanvasTool.Text);

    private void OnModeReplaceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetSelectionMode(SelectionCombine.Replace);

    private void OnModeAddClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetSelectionMode(SelectionCombine.Add);

    private void OnModeSubtractClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetSelectionMode(SelectionCombine.Subtract);

    private void OnModeIntersectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetSelectionMode(SelectionCombine.Intersect);

    private void OnLinearGradientClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetGradientKind(true);

    private void OnRadialGradientClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetGradientKind(false);

    /// <summary>点击图层行切换当前图层。</summary>
    private void OnLayerRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            sender is Control { DataContext: LayerItemViewModel item })
        {
            _viewModel.SelectLayer(item);
        }
    }

    private void OnHistoryRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            sender is Control { DataContext: HistoryItemViewModel item })
        {
            _viewModel.SelectHistory(item);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null || files.Length == 0)
            return;

        var path = files[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
            await _viewModel.OpenPathAsync(path);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // 文本框获得焦点时不拦截快捷键
        if (FocusManager?.GetFocusedElement() is TextBox)
        {
            base.OnKeyDown(e);
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_viewModel.IsTransforming)
            {
                _viewModel.CancelTransformCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (_viewModel.IsCropMode)
            {
                _viewModel.CancelCropCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (_viewModel.HasSelection)
            {
                _viewModel.DeselectCommand.Execute(null);
                e.Handled = true;
                return;
            }
        }

        if (e.Key is Key.Delete or Key.Back && _viewModel.HasSelection)
        {
            _viewModel.DeleteSelectionContentCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // 移动工具下方向键微调（Shift 为 10 像素）
        if (_viewModel.IsMoveTool && _viewModel.HasDocument &&
            e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            var step = e.KeyModifiers == KeyModifiers.Shift ? 10 : 1;
            switch (e.Key)
            {
                case Key.Left:
                    _viewModel.NudgeActiveLayer(-step, 0);
                    e.Handled = true;
                    return;
                case Key.Right:
                    _viewModel.NudgeActiveLayer(step, 0);
                    e.Handled = true;
                    return;
                case Key.Up:
                    _viewModel.NudgeActiveLayer(0, -step);
                    e.Handled = true;
                    return;
                case Key.Down:
                    _viewModel.NudgeActiveLayer(0, step);
                    e.Handled = true;
                    return;
            }
        }

        // 无修饰键：工具切换与笔刷尺寸
        if (e.KeyModifiers == KeyModifiers.None)
        {
            switch (e.Key)
            {
                case Key.B:
                    _viewModel.SetTool(CanvasTool.Brush);
                    e.Handled = true;
                    return;
                case Key.E:
                    _viewModel.SetTool(CanvasTool.Eraser);
                    e.Handled = true;
                    return;
                case Key.G:
                    _viewModel.SetTool(CanvasTool.Gradient);
                    e.Handled = true;
                    return;
                case Key.P:
                    _viewModel.SetTool(CanvasTool.Bucket);
                    e.Handled = true;
                    return;
                case Key.T:
                    _viewModel.SetTool(CanvasTool.Text);
                    e.Handled = true;
                    return;
                case Key.V:
                    _viewModel.SetTool(CanvasTool.Move);
                    e.Handled = true;
                    return;
                case Key.M:
                    _viewModel.SetTool(CanvasTool.SelectRect);
                    e.Handled = true;
                    return;
                case Key.X:
                    _viewModel.SwapColorsCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.OemOpenBrackets:
                    _viewModel.AdjustBrushSize(1 / 1.25);
                    e.Handled = true;
                    return;
                case Key.OemCloseBrackets:
                    _viewModel.AdjustBrushSize(1.25);
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Enter && _viewModel.IsTransforming)
        {
            _viewModel.CommitTransformCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
                case Key.T:
                    _viewModel.BeginTransformCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.A:
                    _viewModel.SelectAllCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.D:
                    _viewModel.DeselectCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.OemPlus:
                case Key.Add:
                    _viewModel.ZoomInCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.OemMinus:
                case Key.Subtract:
                    _viewModel.ZoomOutCommand.Execute(null);
                    e.Handled = true;
                    return;
            }
        }

        base.OnKeyDown(e);
    }
}
