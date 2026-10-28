using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Controls;
using ImageStudio.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// Avalonia 与 ImageSharp 存在同名类型，这里显式指定使用 Avalonia 的几何/颜色类型。
using Point = Avalonia.Point;
using Size = Avalonia.Size;
using Color = Avalonia.Media.Color;

namespace ImageStudio.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly FilePickerFileType[] ImageFileTypes =
    [
        new("图像文件")
        {
            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"],
            MimeTypes = ["image/png", "image/jpeg", "image/bmp", "image/gif", "image/webp"],
            AppleUniformTypeIdentifiers = ["public.image"]
        },
        new("PNG") { Patterns = ["*.png"] },
        new("JPEG") { Patterns = ["*.jpg", "*.jpeg"] },
        new("所有文件") { Patterns = ["*"] }
    ];

    private readonly DispatcherTimer _previewTimer;
    private ImageDocument? _document;
    private WriteableBitmap? _masterBitmap;
    private int _previewVersion;
    private bool _suspendPreview;
    private bool _isBusy;
    private bool _pendingFit;
    private Color _lastSampleColor;

    public MainViewModel()
    {
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _previewTimer.Tick += async (_, _) =>
        {
            _previewTimer.Stop();
            await UpdatePreviewAsync();
        };

        InitLayerTimer();
    }

    /// <summary>由窗口在构造时注入，用于弹出文件对话框。</summary>
    public IStorageProvider? Storage { get; set; }

    public Window? Owner { get; set; }

    // ------------------------------------------------------------------ 画布状态

    [ObservableProperty]
    public partial Bitmap? ImageSource { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    public partial double Zoom { get; set; } = 1;

    [ObservableProperty]
    public partial Point Offset { get; set; }

    [ObservableProperty]
    public partial Size ViewportSize { get; set; }

    [ObservableProperty]
    public partial bool ShowCheckerboard { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CropText))]
    public partial Rect CropRect { get; set; }

    // ------------------------------------------------------------------ 文档状态

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RotateClockwiseCommand))]
    [NotifyCanExecuteChangedFor(nameof(RotateCounterClockwiseCommand))]
    [NotifyCanExecuteChangedFor(nameof(Rotate180Command))]
    [NotifyCanExecuteChangedFor(nameof(RotateByAngleCommand))]
    [NotifyCanExecuteChangedFor(nameof(FlipHorizontalCommand))]
    [NotifyCanExecuteChangedFor(nameof(FlipVerticalCommand))]
    [NotifyCanExecuteChangedFor(nameof(ResizeImageCommand))]
    [NotifyCanExecuteChangedFor(nameof(GrayscaleCommand))]
    [NotifyCanExecuteChangedFor(nameof(InvertCommand))]
    [NotifyCanExecuteChangedFor(nameof(SepiaCommand))]
    [NotifyCanExecuteChangedFor(nameof(EdgesCommand))]
    [NotifyCanExecuteChangedFor(nameof(BlurCommand))]
    [NotifyCanExecuteChangedFor(nameof(SharpenCommand))]
    public partial bool HasDocument { get; set; }

    [ObservableProperty]
    public partial bool IsDirty { get; set; }

    [ObservableProperty]
    public partial string FileName { get; set; } = "未命名";

    [ObservableProperty]
    public partial string FilePathText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ImageSizeText { get; set; } = "—";

    [ObservableProperty]
    public partial string StatusText { get; set; } = "就绪 · 打开或拖入一张图片开始编辑";

    [ObservableProperty]
    public partial string CursorText { get; set; } = "—";

    [ObservableProperty]
    public partial string SampleText { get; set; } = "—";

    [ObservableProperty]
    public partial IBrush SampleBrush { get; set; } = Brushes.Transparent;

    [ObservableProperty]
    public partial bool IsCursorInside { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    // ------------------------------------------------------------------ 调整参数

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAdjustment))]
    public partial double Brightness { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAdjustment))]
    public partial double Contrast { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAdjustment))]
    public partial double Saturation { get; set; }

    [ObservableProperty]
    public partial double BlurRadius { get; set; } = 4;

    [ObservableProperty]
    public partial double SharpenAmount { get; set; } = 1;

    [ObservableProperty]
    public partial double RotateAngle { get; set; } = 15;

    [ObservableProperty]
    public partial double JpegQuality { get; set; } = 92;

    public bool HasAdjustment =>
        Math.Abs(Brightness) > 0.01 || Math.Abs(Contrast) > 0.01 || Math.Abs(Saturation) > 0.01;

    public string ZoomText => $"{Zoom * 100:0}%";

    public string CropText => IsCropMode && CropRect.Width >= 1 && CropRect.Height >= 1
        ? $"{Math.Round(CropRect.Width)} × {Math.Round(CropRect.Height)} px"
        : "拖动以框选区域";

    partial void OnBrightnessChanged(double value) => OnAdjustmentChanged();

    partial void OnContrastChanged(double value) => OnAdjustmentChanged();

    partial void OnSaturationChanged(double value) => OnAdjustmentChanged();

    private void OnAdjustmentChanged()
    {
        ApplyAdjustmentCommand.NotifyCanExecuteChanged();
        SchedulePreview();
    }

    partial void OnCropRectChanged(Rect value) => OnPropertyChanged(nameof(CropText));

    // ------------------------------------------------------------------ 文档生命周期

    public async Task<bool> OpenPathAsync(string path)
    {
        try
        {
            IsBusy = true;
            StatusText = "正在打开 " + Path.GetFileName(path) + " …";

            var image = await Task.Run(() => ImageCodec.Load(path));
            var document = new ImageDocument(image, path);
            SetDocument(document, fit: true);
            StatusText = $"已打开 {Path.GetFileName(path)} · {image.Width} × {image.Height}";
            return true;
        }
        catch (Exception ex)
        {
            StatusText = "打开失败：" + ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetDocument(ImageDocument document, bool fit)
    {
        if (_document is not null)
        {
            _document.Changed -= OnDocumentChanged;
            _document.Dispose();
        }

        _document = document;
        document.Changed += OnDocumentChanged;

        FileName = document.FilePath is null ? "未命名" : Path.GetFileName(document.FilePath);
        FilePathText = document.FilePath ?? "尚未保存";
        HasDocument = true;
        IsDirty = document.IsDirty;
        ActiveTool = CanvasTool.Pan;
        SetSelection(null);
        _suspendPreview = true;
        Brightness = 0;
        Contrast = 0;
        Saturation = 0;
        _suspendPreview = false;

        UpdateSize();
        RefreshBitmap();

        if (fit)
        {
            if (ViewportSize.Width > 10 && ViewportSize.Height > 10)
                FitToWindow();
            else
                _pendingFit = true;
        }
        else
        {
            CenterImage();
        }

        RefreshCommandStates();
    }

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        IsDirty = _document?.IsDirty ?? false;
        UpdateSize();

        // 撤销/重做可能把画布尺寸改回去，此时旧选区已无意义
        if (_selection is { } selection && _document is { } document &&
            (selection.Width != document.Width || selection.Height != document.Height))
            SetSelection(null);

        RefreshBitmap();
        RefreshCommandStates();
    }

    private void UpdateSize()
    {
        if (_document is null)
        {
            ImageSizeText = "—";
            return;
        }

        ImageSizeText = $"{_document.Width} × {_document.Height} px";
    }

    private void RefreshBitmap()
    {
        if (_document is null)
        {
            ImageSource = null;
            _masterBitmap = null;
            RefreshLayers();
            return;
        }

        _masterBitmap = BitmapFactory.ToBitmap(_document.Composite());
        ImageSource = _masterBitmap;
        RefreshLayers();
    }

    public void SetViewport(Size size)
    {
        ViewportSize = size;
        if (_pendingFit && size.Width > 10 && size.Height > 10)
        {
            _pendingFit = false;
            FitToWindow();
        }
    }

    // ------------------------------------------------------------------ 实时预览

    private void SchedulePreview()
    {
        if (_suspendPreview || _document is null || _isBusy)
            return;

        _previewTimer.Stop();
        _previewTimer.Start();
    }

    private async Task UpdatePreviewAsync()
    {
        if (_document is null || _isBusy)
            return;

        var layer = _document.ActiveLayer;

        // 调整只作用于当前图层，因此需要把调整后的图层重新合成到图层栈中预览
        if (!HasAdjustment || layer is null)
        {
            _previewVersion++;
            ImageSource = _masterBitmap;
            return;
        }

        var version = ++_previewVersion;
        var source = layer.Pixels;
        var index = _document.ActiveIndex;
        var document = _document;
        var b = Brightness / 100.0;
        var c = Contrast / 100.0;
        var s = Saturation / 100.0;

        try
        {
            var selection = _selection;
            var adjusted = await Task.Run(() =>
            {
                var result = ImageOps.Adjust(source, b, c, s);
                if (selection is not { IsEmpty: false })
                    return result;

                var blended = ImageOps.BlendByMask(source, result, selection);
                result.Dispose();
                return blended;
            });
            Image<Bgra32>? composed = null;

            try
            {
                composed = await Task.Run(() => document.CompositeWith(index, adjusted));
            }
            finally
            {
                adjusted.Dispose();
            }

            if (version != _previewVersion)
            {
                composed.Dispose();
                return;
            }

            ImageSource = BitmapFactory.ToBitmap(composed);
            composed.Dispose();
        }
        catch (Exception ex)
        {
            StatusText = "预览失败：" + ex.Message;
        }
    }

    // ------------------------------------------------------------------ 提交式操作

    /// <summary>对当前图层做一次可撤销的像素修改（滤镜、色彩调整）。</summary>
    private async Task CommitLayerAsync(string label, Func<Image<Bgra32>, Image<Bgra32>> operation)
    {
        if (_document is null || _isBusy)
            return;

        var layer = _document.ActiveLayer;
        if (layer is null)
            return;

        _previewTimer.Stop();
        _previewVersion++;
        _isBusy = true;
        IsBusy = true;

        var selection = _selection;
        try
        {
            using (_document.BeginEdit(label))
            {
                var source = layer.Pixels;
                var result = await Task.Run(() =>
                {
                    var output = operation(source);
                    if (selection is not { IsEmpty: false })
                        return output;

                    // 有选区时只让操作结果作用在选区内部
                    var blended = ImageOps.BlendByMask(source, output, selection);
                    output.Dispose();
                    return blended;
                });
                layer.ReplacePixels(result);
            }

            ResetAdjustmentSliders();
            StatusText = selection is { IsEmpty: false } ? label + " 已应用到选区" : label + " 已完成";
        }
        catch (Exception ex)
        {
            StatusText = label + " 失败：" + ex.Message;
        }
        finally
        {
            _isBusy = false;
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    /// <summary>对所有图层施加同一种几何变换（裁剪 / 旋转 / 翻转 / 缩放画布）。</summary>
    private async Task CommitDocumentAsync(
        string label,
        Func<Image<Bgra32>, Image<Bgra32>> pixelOp,
        Func<Image<L8>, Image<L8>>? maskOp,
        bool recenter = true)
    {
        if (_document is null || _isBusy)
            return;

        _previewTimer.Stop();
        _previewVersion++;
        _isBusy = true;
        IsBusy = true;

        try
        {
            var layers = _document.Layers.ToList();
            var selection = _selection;

            // 选区也要跟着画布一起变换，否则裁剪/旋转后会与图像错位
            var selectionMask = selection is not null && maskOp is not null ? selection.ToMask() : null;

            var results = await Task.Run(() => layers
                .Select(l => (
                    Pixels: pixelOp(l.Pixels),
                    Mask: l.Mask is null || maskOp is null ? null : maskOp(l.Mask)))
                .ToList());

            Image<L8>? transformedSelection = null;
            if (selectionMask is not null && maskOp is not null)
            {
                transformedSelection = await Task.Run(() => maskOp(selectionMask));
                selectionMask.Dispose();
            }

            if (results.Count == 0)
            {
                transformedSelection?.Dispose();
                return;
            }

            using (_document.BeginEdit(label))
            {
                for (var i = 0; i < layers.Count; i++)
                {
                    layers[i].ReplacePixels(results[i].Pixels);

                    if (layers[i].Mask is not null)
                    {
                        layers[i].Mask!.Dispose();
                        layers[i].Mask = null;
                    }

                    if (results[i].Mask is not null)
                    {
                        layers[i].Mask = results[i].Mask;
                        layers[i].MaskEnabled = true;
                    }
                }

                _document.SetSize(results[0].Pixels.Width, results[0].Pixels.Height);
            }

            if (transformedSelection is not null)
            {
                SetSelection(Selection.FromMask(transformedSelection));
                transformedSelection.Dispose();
            }

            ResetAdjustmentSliders();

            if (recenter)
                CenterImage();

            StatusText = label + " 已完成";
        }
        catch (Exception ex)
        {
            StatusText = label + " 失败：" + ex.Message;
        }
        finally
        {
            _isBusy = false;
            IsBusy = false;
            RefreshCommandStates();
        }
    }

    private void ResetAdjustmentSliders()
    {
        _suspendPreview = true;
        Brightness = 0;
        Contrast = 0;
        Saturation = 0;
        _suspendPreview = false;
    }

    // ------------------------------------------------------------------ 文件命令

    [RelayCommand]
    private async Task NewAsync()
    {
        if (Owner is null)
            return;

        var dialog = new Views.SizeDialog("新建画布", 1920, 1080, "创建");
        var ok = await dialog.ShowDialog<bool>(Owner);
        if (!ok)
            return;

        var (w, h) = (dialog.ResultWidth, dialog.ResultHeight);
        var image = await Task.Run(() =>
        {
            var img = new Image<Bgra32>(w, h);
            img.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                    accessor.GetRowSpan(y).Fill(new Bgra32 { R = 255, G = 255, B = 255, A = 255 });
            });
            return img;
        });

        SetDocument(new ImageDocument(image), fit: true);
        StatusText = $"已新建 {w} × {h} 画布";
    }

    [RelayCommand]
    private async Task OpenAsync()
    {
        if (Storage is null)
            return;

        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开图片",
            AllowMultiple = false,
            FileTypeFilter = ImageFileTypes
        });

        if (files.Count == 0)
            return;

        var path = files[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
        {
            StatusText = "无法访问所选文件的本地路径";
            return;
        }

        await OpenPathAsync(path);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_document is null)
            return;

        if (string.IsNullOrEmpty(_document.FilePath))
        {
            await SaveAsAsync();
            return;
        }

        await SaveToAsync(_document.FilePath);
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (_document is null || Storage is null)
            return;

        var suggested = string.IsNullOrEmpty(_document.FilePath)
            ? "未命名.png"
            : Path.GetFileName(_document.FilePath);

        var file = await Storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "另存为",
            SuggestedFileName = suggested,
            DefaultExtension = "png",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("PNG 图像") { Patterns = ["*.png"] },
                new FilePickerFileType("JPEG 图像") { Patterns = ["*.jpg", "*.jpeg"] },
                new FilePickerFileType("BMP 图像") { Patterns = ["*.bmp"] },
                new FilePickerFileType("GIF 图像") { Patterns = ["*.gif"] },
                new FilePickerFileType("WEBP 图像") { Patterns = ["*.webp"] }
            ]
        });

        if (file is null)
            return;

        var path = file.TryGetLocalPath();
        if (string.IsNullOrEmpty(path))
            return;

        // 没有扩展名时按选择器默认补 .png
        if (string.IsNullOrEmpty(Path.GetExtension(path)))
            path += ".png";

        await SaveToAsync(path);
    }

    private async Task SaveToAsync(string path)
    {
        if (_document is null)
            return;

        try
        {
            IsBusy = true;
            StatusText = "正在保存 " + Path.GetFileName(path) + " …";
            var image = _document.Composite();
            var kind = ImageCodec.KindFromPath(path);
            var quality = (int)JpegQuality;
            await Task.Run(() => ImageCodec.Save(image, path, kind, quality));

            _document.FilePath = path;
            _document.IsDirty = false;
            FileName = Path.GetFileName(path);
            FilePathText = path;
            IsDirty = false;
            StatusText = $"已保存到 {path}";
        }
        catch (Exception ex)
        {
            StatusText = "保存失败：" + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Exit() => Owner?.Close();

    // ------------------------------------------------------------------ 编辑命令

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_document is null)
            return;

        _document.Undo();
        ImageSource = _masterBitmap;
        CenterImage();
        StatusText = "已撤销";
    }

    private bool CanUndo() => _document?.CanUndo == true;

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_document is null)
            return;

        _document.Redo();
        ImageSource = _masterBitmap;
        CenterImage();
        StatusText = "已重做";
    }

    private bool CanRedo() => _document?.CanRedo == true;

    // ------------------------------------------------------------------ 图像命令

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task RotateClockwiseAsync() =>
        CommitDocumentAsync("顺时针旋转 90°",
            img => ImageOps.RotateQuarter(img, true),
            mask => ImageOps.RotateQuarter(mask, true));

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task RotateCounterClockwiseAsync() =>
        CommitDocumentAsync("逆时针旋转 90°",
            img => ImageOps.RotateQuarter(img, false),
            mask => ImageOps.RotateQuarter(mask, false));

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task Rotate180Async() =>
        CommitDocumentAsync("旋转 180°", img => ImageOps.Rotate180(img), mask => ImageOps.Rotate180(mask));

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task RotateByAngleAsync()
    {
        var angle = RotateAngle;
        await CommitDocumentAsync($"旋转 {angle:0.#}°",
            img => ImageOps.RotateArbitrary(img, angle),
            mask => ImageOps.RotateArbitraryMask(mask, angle),
            recenter: false);
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task FlipHorizontalAsync() =>
        CommitDocumentAsync("水平翻转", img => ImageOps.Flip(img, true), mask => ImageOps.Flip(mask, true));

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task FlipVerticalAsync() =>
        CommitDocumentAsync("垂直翻转", img => ImageOps.Flip(img, false), mask => ImageOps.Flip(mask, false));

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void BeginCrop()
    {
        if (_document is null)
            return;

        CropRect = new Rect(0, 0, _document.Width, _document.Height);
        ActiveTool = CanvasTool.Crop;
        StatusText = "裁剪：在画布上拖动框选区域，然后点击“应用裁剪”";
    }

    [RelayCommand]
    private void CancelCrop()
    {
        ActiveTool = CanvasTool.Pan;
        StatusText = "已取消裁剪";
    }

    [RelayCommand]
    private async Task ApplyCropAsync()
    {
        if (_document is null || CropRect.Width < 1 || CropRect.Height < 1)
            return;

        var rect = new Rectangle(
            (int)Math.Round(CropRect.X),
            (int)Math.Round(CropRect.Y),
            (int)Math.Round(CropRect.Width),
            (int)Math.Round(CropRect.Height));

        rect = Rectangle.Intersect(rect, new Rectangle(0, 0, _document.Width, _document.Height));
        if (rect.Width < 1 || rect.Height < 1)
            return;

        ActiveTool = CanvasTool.Pan;
        await CommitDocumentAsync($"裁剪 {rect.Width}×{rect.Height}",
            img => ImageOps.Crop(img, rect),
            mask => ImageOps.Crop(mask, rect));
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task ResizeImageAsync()
    {
        if (_document is null || Owner is null)
            return;

        var dialog = new Views.SizeDialog("调整图像大小", _document.Width, _document.Height, "应用");
        var ok = await dialog.ShowDialog<bool>(Owner);
        if (!ok)
            return;

        var (w, h) = (dialog.ResultWidth, dialog.ResultHeight);
        await CommitDocumentAsync($"缩放为 {w}×{h}",
            img => ImageOps.Resize(img, w, h),
            mask => ImageOps.Resize(mask, w, h));
    }

    // ------------------------------------------------------------------ 调整 / 滤镜

    [RelayCommand(CanExecute = nameof(HasAdjustment))]
    private async Task ApplyAdjustmentAsync()
    {
        var b = Brightness / 100.0;
        var c = Contrast / 100.0;
        var s = Saturation / 100.0;
        await CommitLayerAsync("调整颜色", img => ImageOps.Adjust(img, b, c, s));
    }

    [RelayCommand]
    private void ResetAdjustment()
    {
        _suspendPreview = true;
        Brightness = 0;
        Contrast = 0;
        Saturation = 0;
        _suspendPreview = false;
        ImageSource = _masterBitmap;
        StatusText = "已重置调整参数";
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task GrayscaleAsync() => CommitLayerAsync("灰度", ImageOps.Grayscale);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task InvertAsync() => CommitLayerAsync("反色", ImageOps.Invert);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task SepiaAsync() => CommitLayerAsync("棕褐色", ImageOps.Sepia);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private Task EdgesAsync() => CommitLayerAsync("边缘检测", ImageOps.Edges);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task BlurAsync()
    {
        var radius = (float)BlurRadius;
        await CommitLayerAsync($"高斯模糊 {radius:0.#}px", img => ImageOps.Blur(img, radius));
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task SharpenAsync()
    {
        var amount = (float)SharpenAmount;
        await CommitLayerAsync($"锐化 {amount:0.#}", img => ImageOps.Sharpen(img, amount));
    }

    // ------------------------------------------------------------------ 视图命令

    [RelayCommand]
    private void ZoomIn() => ZoomAtCenter(1.25);

    [RelayCommand]
    private void ZoomOut() => ZoomAtCenter(1 / 1.25);

    [RelayCommand]
    private void ZoomActual()
    {
        if (_document is null)
            return;

        Zoom = 1;
        CenterImage();
        StatusText = "缩放 100%";
    }

    [RelayCommand]
    private void FitToWindow()
    {
        if (_document is null)
            return;

        var vw = ViewportSize.Width;
        var vh = ViewportSize.Height;
        if (vw < 20 || vh < 20)
        {
            _pendingFit = true;
            return;
        }

        var zoom = Math.Min((vw - 72) / _document.Width, (vh - 72) / _document.Height);
        Zoom = Math.Clamp(zoom, 0.02, 8);
        CenterImage();
        StatusText = "适应窗口";
    }

    [RelayCommand]
    private void ToggleCheckerboard() => ShowCheckerboard = !ShowCheckerboard;

    private void ZoomAtCenter(double factor)
    {
        if (_document is null)
            return;

        var anchor = new Point(ViewportSize.Width / 2, ViewportSize.Height / 2);
        var target = Math.Clamp(Zoom * factor, 0.02, 40);
        var ratio = target / Zoom;
        Offset = new Point(
            anchor.X - (anchor.X - Offset.X) * ratio,
            anchor.Y - (anchor.Y - Offset.Y) * ratio);
        Zoom = target;
    }

    private void CenterImage()
    {
        if (_document is null)
            return;

        var w = _document.Width * Zoom;
        var h = _document.Height * Zoom;
        Offset = new Point((ViewportSize.Width - w) / 2, (ViewportSize.Height - h) / 2);
    }

    // ------------------------------------------------------------------ 画布回调

    public void OnCanvasPointer(Point imagePosition, bool isInside)
    {
        if (_document is null || !isInside)
        {
            IsCursorInside = false;
            CursorText = "—";
            return;
        }

        var x = (int)imagePosition.X;
        var y = (int)imagePosition.Y;
        if (x < 0 || y < 0 || x >= _document.Width || y >= _document.Height)
        {
            IsCursorInside = false;
            CursorText = "—";
            return;
        }

        IsCursorInside = true;
        CursorText = $"{x}, {y}";

        var pixel = _document.Composite()[x, y];
        var color = Color.FromArgb(pixel.A, pixel.R, pixel.G, pixel.B);
        if (color != _lastSampleColor || string.IsNullOrEmpty(SampleText))
        {
            _lastSampleColor = color;
            SampleBrush = new SolidColorBrush(color);
            SampleText = $"#{pixel.R:X2}{pixel.G:X2}{pixel.B:X2}";
        }
    }

    public void StatusHint(string message) => StatusText = message;

    public void RefreshCommandStates()
    {
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        ApplyAdjustmentCommand.NotifyCanExecuteChanged();
        NotifyLayerCommands();
        NotifySelectionCommands();
        OnPropertyChanged(nameof(HasDocument));
    }

    public void Dispose()
    {
        _previewTimer.Stop();
        _layerTimer.Stop();
        if (_document is not null)
            _document.Changed -= OnDocumentChanged;
        _document?.Dispose();
        _document = null;
    }
}
