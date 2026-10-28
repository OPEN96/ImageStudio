using System;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageStudio.Models;

/// <summary>
/// 文档中的一步历史记录（保存整张图像的快照）。
/// </summary>
internal sealed class HistoryEntry(string label, Image<Bgra32> snapshot, long bytes)
{
    public string Label { get; } = label;
    public Image<Bgra32> Snapshot { get; } = snapshot;
    public long Bytes { get; } = bytes;
}

/// <summary>
/// 一张被编辑的图像：拥有像素数据、撤销/重做栈以及保存状态。
/// </summary>
public sealed class ImageDocument : IDisposable
{
    private const int MaxHistory = 16;
    private const long MaxHistoryBytes = 512L * 1024 * 1024;

    private readonly List<HistoryEntry> _undo = [];
    private readonly List<HistoryEntry> _redo = [];

    public ImageDocument(Image<Bgra32> image, string? filePath = null)
    {
        Image = image;
        FilePath = filePath;
    }

    public Image<Bgra32> Image { get; private set; }
    public string? FilePath { get; set; }
    public bool IsDirty { get; set; }

    public int Width => Image.Width;
    public int Height => Image.Height;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;
    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    public event EventHandler? Changed;

    /// <summary>提交一次修改：先把当前状态压入撤销栈，再换上新图。</summary>
    public void Commit(string label, Image<Bgra32> newImage)
    {
        if (ReferenceEquals(newImage, Image))
            return;

        var snapshot = Image.Clone();
        _undo.Add(new HistoryEntry(label, snapshot, EstimateBytes(Image)));
        TrimHistory();
        _redo.Clear();

        var previous = Image;
        Image = newImage;
        IsDirty = true;
        previous.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (_undo.Count == 0)
            return;

        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(new HistoryEntry(entry.Label, Image, EstimateBytes(Image)));
        Image = entry.Snapshot;
        IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0)
            return;

        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(new HistoryEntry(entry.Label, Image, EstimateBytes(Image)));
        Image = entry.Snapshot;
        IsDirty = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearHistory()
    {
        foreach (var item in _undo) item.Snapshot.Dispose();
        foreach (var item in _redo) item.Snapshot.Dispose();
        _undo.Clear();
        _redo.Clear();
    }

    private void TrimHistory()
    {
        long total = 0;
        foreach (var entry in _undo) total += entry.Bytes;

        while (_undo.Count > 1 && (_undo.Count > MaxHistory || total > MaxHistoryBytes))
        {
            total -= _undo[0].Bytes;
            _undo[0].Snapshot.Dispose();
            _undo.RemoveAt(0);
        }
    }

    private static long EstimateBytes(Image<Bgra32> image) => (long)image.Width * image.Height * 4;

    public void Dispose()
    {
        ClearHistory();
        Image.Dispose();
    }
}
