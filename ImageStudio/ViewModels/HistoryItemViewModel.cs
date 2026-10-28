using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ImageStudio.ViewModels;

/// <summary>
/// 历史面板的一行：表示一个可回溯的编辑状态。
/// 显示顺序是新的在上、旧的在下，因此它与文档里的状态下标相反。
/// </summary>
public sealed partial class HistoryItemViewModel : ObservableObject
{
    public HistoryItemViewModel(int stateIndex, string label, int step)
    {
        StateIndex = stateIndex;
        Label = label;
        StepText = step.ToString();
    }

    /// <summary>对应的文档历史状态下标（0 最早）。</summary>
    public int StateIndex { get; }

    public string Label { get; }

    /// <summary>第几步（0 为打开时的初始状态）。</summary>
    public string StepText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    public partial Bitmap? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>位于当前状态之后（还能重做到达）。</summary>
    [ObservableProperty]
    public partial bool IsFuture { get; set; }

    /// <summary>尝试生成过缩略图但没有数据，避免后台任务反复重试。</summary>
    public bool ThumbnailFailed { get; set; }

    public bool HasThumbnail => Thumbnail is not null;

    public void SetThumbnail(Bitmap bitmap)
    {
        Thumbnail?.Dispose();
        Thumbnail = bitmap;
    }
}
