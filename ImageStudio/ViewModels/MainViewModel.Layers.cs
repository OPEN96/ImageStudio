using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageStudio.Models;

namespace ImageStudio.ViewModels;

/// <summary>混合模式下拉框的选项。</summary>
public sealed record BlendModeOption(BlendMode Mode, string Name);

/// <summary>图层面板相关的状态与命令。</summary>
public partial class MainViewModel
{
    private readonly DispatcherTimer _layerTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private bool _syncingLayers;

    public IReadOnlyList<BlendModeOption> BlendModeOptions { get; } =
        BlendModes.All.Select(x => new BlendModeOption(x.Mode, x.Name)).ToArray();

    /// <summary>图层列表，索引 0 是最上层（与面板显示顺序一致）。</summary>
    public ObservableCollection<LayerItemViewModel> LayerItems { get; } = [];

    [ObservableProperty]
    public partial string ActiveLayerName { get; set; } = "—";

    [ObservableProperty]
    public partial string LayerCountText { get; set; } = "0 个图层";

    [ObservableProperty]
    public partial double LayerOpacity { get; set; } = 100;

    [ObservableProperty]
    public partial BlendModeOption? ActiveBlendMode { get; set; }

    [ObservableProperty]
    public partial bool HasActiveMask { get; set; }

    [ObservableProperty]
    public partial bool ActiveMaskEnabled { get; set; } = true;

    private void InitLayerTimer()
    {
        _layerTimer.Tick += (_, _) =>
        {
            _layerTimer.Stop();
            _document?.Touch();
        };
    }

    // ------------------------------------------------------------------ 同步

    /// <summary>把文档的图层状态同步到面板（按图层对象复用条目，避免列表闪烁）。</summary>
    private void RefreshLayers()
    {
        if (_document is null)
        {
            LayerItems.Clear();
            ActiveLayerName = "—";
            LayerCountText = "0 个图层";
            HasActiveMask = false;
            NotifyLayerCommands();
            return;
        }

        var layers = _document.Layers;
        var needsRebuild = LayerItems.Count != layers.Count;

        if (!needsRebuild)
        {
            for (var i = 0; i < layers.Count; i++)
            {
                if (ReferenceEquals(LayerItems[LayerItems.Count - 1 - i].Layer, layers[i]))
                    continue;

                needsRebuild = true;
                break;
            }
        }

        _syncingLayers = true;
        try
        {
            if (needsRebuild)
            {
                LayerItems.Clear();
                for (var i = layers.Count - 1; i >= 0; i--)
                    LayerItems.Add(new LayerItemViewModel(layers[i], OnLayerVisibilityToggled));
            }

            var active = _document.ActiveLayer;

            foreach (var item in LayerItems)
            {
                item.SyncFromLayer();
                item.RefreshThumbnail();
                item.IsSelected = ReferenceEquals(item.Layer, active);
            }

            ActiveLayerName = active?.Name ?? "—";
            LayerCountText = $"{layers.Count} 个图层";

            if (active is not null)
            {
                LayerOpacity = Math.Round(active.Opacity * 100);
                ActiveBlendMode = BlendModeOptions.FirstOrDefault(o => o.Mode == active.BlendMode)
                                  ?? BlendModeOptions[0];
                HasActiveMask = active.HasMask;
                ActiveMaskEnabled = active.MaskEnabled;
            }
        }
        finally
        {
            _syncingLayers = false;
        }

        NotifyLayerCommands();
    }

    private void OnLayerVisibilityToggled(LayerItemViewModel item)
    {
        if (_document is null || _syncingLayers)
            return;

        _document.Touch();
        StatusText = (item.IsVisible ? "已显示图层「" : "已隐藏图层「") + item.Name + "」";
    }

    private void ScheduleLayerRefresh()
    {
        if (_syncingLayers || _document is null)
            return;

        _layerTimer.Stop();
        _layerTimer.Start();
    }

    partial void OnLayerOpacityChanged(double value)
    {
        if (_syncingLayers || _document?.ActiveLayer is not { } layer)
            return;

        layer.Opacity = Math.Clamp(value / 100.0, 0, 1);
        ScheduleLayerRefresh();
    }

    partial void OnActiveBlendModeChanged(BlendModeOption? value)
    {
        if (_syncingLayers || value is null || _document?.ActiveLayer is not { } layer)
            return;

        if (layer.BlendMode == value.Mode)
            return;

        using (_document.BeginEdit("混合模式"))
            layer.BlendMode = value.Mode;

        StatusText = $"图层「{layer.Name}」混合模式：{value.Name}";
    }

    // ------------------------------------------------------------------ 选中

    public void SelectLayer(LayerItemViewModel item)
    {
        if (_document is null)
            return;

        var index = _document.IndexOfLayer(item.Layer);
        if (index < 0)
            return;

        _document.ActiveIndex = index;
        RefreshLayers();
        StatusText = $"当前图层：{item.Name}";
    }

    // ------------------------------------------------------------------ 命令

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void NewLayer()
    {
        if (_document is null)
            return;

        var layer = _document.AddLayer($"图层 {_document.LayerCount}");
        StatusText = $"已新建图层「{layer.Name}」";
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void DuplicateLayer()
    {
        if (_document is null)
            return;

        _document.DuplicateLayer(_document.ActiveIndex);
        StatusText = "已复制图层";
    }

    [RelayCommand(CanExecute = nameof(CanDeleteLayer))]
    private void DeleteLayer()
    {
        if (_document is null)
            return;

        var name = _document.ActiveLayer?.Name ?? "图层";
        _document.DeleteLayer(_document.ActiveIndex);
        StatusText = $"已删除图层「{name}」";
    }

    private bool CanDeleteLayer() => _document is { LayerCount: > 1 };

    [RelayCommand(CanExecute = nameof(CanMoveLayerUp))]
    private void MoveLayerUp()
    {
        if (_document is null)
            return;

        _document.MoveLayer(_document.ActiveIndex, _document.ActiveIndex + 1);
        StatusText = "已上移图层";
    }

    private bool CanMoveLayerUp() =>
        _document is not null && _document.ActiveIndex < _document.LayerCount - 1;

    [RelayCommand(CanExecute = nameof(CanMoveLayerDown))]
    private void MoveLayerDown()
    {
        if (_document is null)
            return;

        _document.MoveLayer(_document.ActiveIndex, _document.ActiveIndex - 1);
        StatusText = "已下移图层";
    }

    private bool CanMoveLayerDown() => _document is not null && _document.ActiveIndex > 0;

    [RelayCommand(CanExecute = nameof(CanMergeDown))]
    private void MergeDown()
    {
        if (_document is null)
            return;

        _document.MergeDown(_document.ActiveIndex);
        StatusText = "已向下合并";
    }

    private bool CanMergeDown() => _document is not null && _document.CanMergeDown(_document.ActiveIndex);

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private void Flatten()
    {
        if (_document is null)
            return;

        _document.Flatten();
        StatusText = "已拼合图像";
    }

    [RelayCommand(CanExecute = nameof(CanAddMask))]
    private void AddMask()
    {
        if (_document is null)
            return;

        _document.AddMaskFromLuminance(_document.ActiveIndex);
        StatusText = "已按亮度生成图层蒙版";
    }

    private bool CanAddMask() => _document?.ActiveLayer is { HasMask: false };

    [RelayCommand(CanExecute = nameof(HasActiveMask))]
    private void RemoveMask()
    {
        if (_document is null)
            return;

        _document.RemoveMask(_document.ActiveIndex);
        StatusText = "已删除图层蒙版";
    }

    [RelayCommand(CanExecute = nameof(HasActiveMask))]
    private void ToggleMask()
    {
        if (_document?.ActiveLayer is not { } layer)
            return;

        _document.SetMaskEnabled(_document.ActiveIndex, !layer.MaskEnabled);
        StatusText = layer.MaskEnabled ? "已启用蒙版" : "已停用蒙版";
    }

    [RelayCommand(CanExecute = nameof(HasDocument))]
    private async Task RenameLayerAsync()
    {
        if (_document?.ActiveLayer is not { } layer || Owner is null)
            return;

        var dialog = new Views.TextDialog("重命名图层", layer.Name);
        var ok = await dialog.ShowDialog<bool>(Owner);
        if (!ok)
            return;

        using (_document.BeginEdit("重命名图层"))
            layer.Name = dialog.ResultText;

        StatusText = $"图层已重命名为「{dialog.ResultText}」";
    }

    private void NotifyLayerCommands()
    {
        NewLayerCommand.NotifyCanExecuteChanged();
        DuplicateLayerCommand.NotifyCanExecuteChanged();
        DeleteLayerCommand.NotifyCanExecuteChanged();
        MoveLayerUpCommand.NotifyCanExecuteChanged();
        MoveLayerDownCommand.NotifyCanExecuteChanged();
        MergeDownCommand.NotifyCanExecuteChanged();
        FlattenCommand.NotifyCanExecuteChanged();
        AddMaskCommand.NotifyCanExecuteChanged();
        RemoveMaskCommand.NotifyCanExecuteChanged();
        ToggleMaskCommand.NotifyCanExecuteChanged();
        RenameLayerCommand.NotifyCanExecuteChanged();
    }
}
