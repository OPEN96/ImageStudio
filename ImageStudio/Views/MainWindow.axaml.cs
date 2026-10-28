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

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Closing += (_, _) => _viewModel.Dispose();
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

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _viewModel.Storage = StorageProvider;
        _viewModel.Owner = this;
        _viewModel.SetViewport(Canvas.Bounds.Size);
    }

    // ------------------------------------------------------------------ 工具

    private void OnPanToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetTool(CanvasTool.Pan);

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

    private void OnModeReplaceClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetSelectionMode(SelectionCombine.Replace);

    private void OnModeAddClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetSelectionMode(SelectionCombine.Add);

    private void OnModeSubtractClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetSelectionMode(SelectionCombine.Subtract);

    private void OnModeIntersectClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        _viewModel.SetSelectionMode(SelectionCombine.Intersect);

    /// <summary>点击图层行切换当前图层。</summary>
    private void OnLayerRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed &&
            sender is Control { DataContext: LayerItemViewModel item })
        {
            _viewModel.SelectLayer(item);
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

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
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
