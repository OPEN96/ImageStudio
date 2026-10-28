using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
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

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Closing += (_, _) => _viewModel.Dispose();
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

    private void OnPanToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _viewModel.IsCropMode = false;
        _viewModel.StatusHint("平移工具：按住左键拖动画布，滚轮缩放");
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
        if (e.Key == Key.Escape && _viewModel.IsCropMode)
        {
            _viewModel.IsCropMode = false;
            _viewModel.StatusHint("已取消裁剪");
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            switch (e.Key)
            {
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
