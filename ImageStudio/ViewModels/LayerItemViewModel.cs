using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageStudio.Models;

namespace ImageStudio.ViewModels;

/// <summary>图层列表行的视图模型，包装 <see cref="Layer"/> 并提供缩略图。</summary>
public partial class LayerItemViewModel : ObservableObject
{
    private readonly Action<LayerItemViewModel>? _onVisibilityChanged;
    private bool _suppress;
    private int _thumbnailVersion = -1;

    public LayerItemViewModel(Layer layer, Action<LayerItemViewModel>? onVisibilityChanged = null)
    {
        Layer = layer;
        _onVisibilityChanged = onVisibilityChanged;

        _suppress = true;
        Name = layer.Name;
        IsVisible = layer.IsVisible;
        _suppress = false;
    }

    public Layer Layer { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsVisible { get; set; } = true;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    partial void OnIsVisibleChanged(bool value)
    {
        Layer.IsVisible = value;
        if (!_suppress)
            _onVisibilityChanged?.Invoke(this);
    }

    /// <summary>从图层同步名称 / 混合模式等展示信息。</summary>
    public void SyncFromLayer()
    {
        _suppress = true;
        Name = Layer.Name;
        IsVisible = Layer.IsVisible;
        _suppress = false;

        var blend = BlendModes.NameOf(Layer.BlendMode);
        var opacity = Math.Round(Layer.Opacity * 100);

        var text = Math.Abs(opacity - 100) < 0.5 ? blend : $"{blend} · {opacity:0}%";
        if (Layer.HasMask)
            text += Layer.MaskEnabled ? " · 蒙版" : " · 蒙版(关)";

        Subtitle = text;
    }

    /// <summary>像素内容变化时重建缩略图。</summary>
    public void RefreshThumbnail()
    {
        if (_thumbnailVersion == Layer.ContentVersion)
            return;

        _thumbnailVersion = Layer.ContentVersion;
        Thumbnail = BitmapFactory.ToThumbnail(Layer.Pixels);
    }
}
