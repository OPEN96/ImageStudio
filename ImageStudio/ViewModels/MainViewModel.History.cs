using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageStudio.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageStudio.ViewModels;

/// <summary>历史记录面板：状态列表、任意跳转，以及惰性生成的缩略图。</summary>
public partial class MainViewModel
{
    private const int ThumbnailSize = 44;

    /// <summary>历史条目，最新的状态排在最上面。</summary>
    public ObservableCollection<HistoryItemViewModel> HistoryItems { get; } = [];

    private int _historyVersion;
    private bool _historyThumbBusy;

    [ObservableProperty]
    public partial string HistoryCountText { get; set; } = "—";

    public string UndoText =>
        _document?.UndoLabel is { } undo ? $"撤销「{undo}」(Ctrl+Z)" : "撤销 (Ctrl+Z)";

    public string RedoText =>
        _document?.RedoLabel is { } redo ? $"重做「{redo}」(Ctrl+Y)" : "重做 (Ctrl+Y)";

    /// <summary>点击历史面板某一行：直接把画面恢复到该状态。</summary>
    public void SelectHistory(HistoryItemViewModel item)
    {
        if (_document is null || item.StateIndex == _document.HistoryIndex)
            return;

        if (!_document.JumpToHistory(item.StateIndex))
            return;

        ImageSource = _masterBitmap;
        CenterImage();
        StatusText = $"已回到历史状态「{_document.HistoryLabels[item.StateIndex]}」";
    }

    /// <summary>把文档的编辑历史同步到面板。</summary>
    private void RefreshHistory()
    {
        if (_document is null)
        {
            ClearHistoryItems();
            return;
        }

        var labels = _document.HistoryLabels;
        var current = _document.HistoryIndex;

        // 状态数量或标签变化时重建；否则只刷新“当前 / 未来”标记，避免列表闪烁
        var rebuild = HistoryItems.Count != labels.Count;
        if (!rebuild)
        {
            for (var i = 0; i < labels.Count; i++)
            {
                if (HistoryItems[DisplayFromState(i)].Label == labels[i])
                    continue;

                rebuild = true;
                break;
            }
        }

        if (rebuild)
        {
            ClearHistoryItems();
            // 新的在上：显示下标 = 状态总数 - 1 - 状态下标
            for (var i = labels.Count - 1; i >= 0; i--)
                HistoryItems.Add(new HistoryItemViewModel(i, labels[i], i));
        }

        foreach (var item in HistoryItems)
        {
            item.IsCurrent = item.StateIndex == current;
            item.IsFuture = item.StateIndex > current;
        }

        HistoryCountText = $"{labels.Count} 个状态 · 第 {current} 步";
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
        ScheduleHistoryThumbnails();
    }

    private void ClearHistoryItems()
    {
        foreach (var item in HistoryItems)
            item.Thumbnail?.Dispose();
        HistoryItems.Clear();
        HistoryCountText = "—";
    }

    private int DisplayFromState(int stateIndex) => HistoryItems.Count - 1 - stateIndex;

    // ------------------------------------------------------------------ 缩略图

    private void ScheduleHistoryThumbnails()
    {
        if (_document is null)
            return;

        _historyVersion++;

        // 当前状态没有快照，直接用画布正在显示的内容补一张（合成缓存刚被画布用过）
        ApplyCurrentThumbnail();

        if (_historyThumbBusy)
            return;

        _ = Task.Run(RunHistoryThumbnailsAsync);
    }

    private void ApplyCurrentThumbnail()
    {
        if (_document is null)
            return;

        var index = _document.HistoryIndex;
        var item = HistoryItems.FirstOrDefault(x => x.StateIndex == index && x.Thumbnail is null);
        if (item is null)
            return;

        try
        {
            using var thumb = _document.CurrentThumbnail(ThumbnailSize);
            item.SetThumbnail(BitmapFactory.ToBitmap(thumb));
        }
        catch (Exception)
        {
            item.ThumbnailFailed = true;
        }
    }

    /// <summary>逐个补齐历史状态的缩略图：像素工作在后台线程，位图在 UI 线程落地。</summary>
    private async Task RunHistoryThumbnailsAsync()
    {
        _historyThumbBusy = true;
        try
        {
            while (_document is not null)
            {
                var version = _historyVersion;
                var document = _document;
                if (document is null)
                    return;

                var item = await Dispatcher.UIThread.InvokeAsync(
                    () => HistoryItems
                        .Where(x => x.Thumbnail is null && !x.ThumbnailFailed)
                        .OrderBy(x => Math.Abs(x.StateIndex - document.HistoryIndex))
                        .FirstOrDefault());

                if (item is null)
                    return;

                var stateIndex = item.StateIndex;
                Image<Bgra32>? pixels = null;

                try
                {
                    pixels = await Task.Run(() => document.GetHistoryThumbnail(stateIndex, ThumbnailSize));
                    if (pixels is null)
                        await MarkThumbnailFailedAsync(stateIndex);
                    else
                        await Dispatcher.UIThread.InvokeAsync(() => ApplyThumbnail(stateIndex, pixels));
                }
                catch (Exception)
                {
                    await MarkThumbnailFailedAsync(stateIndex);
                }
                finally
                {
                    pixels?.Dispose();
                }

                if (_historyVersion != version)
                    continue;
            }
        }
        finally
        {
            _historyThumbBusy = false;
        }
    }

    private void ApplyThumbnail(int stateIndex, Image<Bgra32> pixels)
    {
        var item = HistoryItems.FirstOrDefault(x => x.StateIndex == stateIndex);
        if (item is null)
            return;

        item.SetThumbnail(BitmapFactory.ToBitmap(pixels));
    }

    private async Task MarkThumbnailFailedAsync(int stateIndex)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var item = HistoryItems.FirstOrDefault(x => x.StateIndex == stateIndex);
            if (item is not null)
                item.ThumbnailFailed = true;
        });
    }
}
