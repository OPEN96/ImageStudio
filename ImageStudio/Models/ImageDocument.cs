using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageStudio.Models;

/// <summary>
/// 一幅被编辑的图像：拥有图层栈、合成缓存、撤销/重做栈以及保存状态。
/// 图层索引 0 表示最底层。
/// </summary>
public sealed class ImageDocument : IDisposable
{
    private const int MaxHistory = 20;
    private const long MaxHistoryBytes = 768L * 1024 * 1024;

    private readonly List<Snapshot> _undo = [];
    private readonly List<Snapshot> _redo = [];
    private readonly List<Layer> _layers = [];

    private Image<Bgra32>? _composite;
    private int _activeIndex;
    private int _editDepth;

    public ImageDocument(Image<Bgra32> image, string? filePath = null)
    {
        _layers.Add(new Layer("背景", image));
        Width = image.Width;
        Height = image.Height;
        FilePath = filePath;
    }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public string? FilePath { get; set; }

    public bool IsDirty { get; set; }

    public IReadOnlyList<Layer> Layers => _layers;

    public int LayerCount => _layers.Count;

    /// <summary>当前活动图层索引，赋值会被限制在合法范围内。</summary>
    public int ActiveIndex
    {
        get => _activeIndex;
        set => _activeIndex = _layers.Count == 0 ? 0 : Math.Clamp(value, 0, _layers.Count - 1);
    }

    public Layer? ActiveLayer =>
        _layers.Count == 0 ? null : _layers[Math.Clamp(_activeIndex, 0, _layers.Count - 1)];

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public string? UndoLabel => _undo.Count > 0 ? _undo[^1].Label : null;

    public string? RedoLabel => _redo.Count > 0 ? _redo[^1].Label : null;

    /// <summary>像素或图层结构发生变化时触发（已在编辑作用域结束时合并）。</summary>
    public event EventHandler? Changed;

    // ------------------------------------------------------------------ 合成

    /// <summary>合成结果缓存，只读，调用方不要释放或修改。</summary>
    public Image<Bgra32> Composite() => _composite ??= LayerCompositor.Composite(Width, Height, _layers);

    /// <summary>带预览覆盖层的临时合成（不写缓存），用于实时预览。</summary>
    public Image<Bgra32> CompositeWith(int index, Image<Bgra32> pixels, Image<L8>? mask = null) =>
        LayerCompositor.Composite(Width, Height, _layers, index, pixels, mask);

    public Layer? LayerAt(int index) =>
        index >= 0 && index < _layers.Count ? _layers[index] : null;

    public int IndexOfLayer(Layer layer) => _layers.IndexOf(layer);

    public void Invalidate()
    {
        _composite?.Dispose();
        _composite = null;
    }

    /// <summary>画布尺寸变化后同步（在编辑作用域内调用，由调用方保证各图层尺寸已一致）。</summary>
    public void SetSize(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Invalidate();
    }

    // ------------------------------------------------------------------ 编辑作用域

    /// <summary>
    /// 开始一次可撤销的编辑。作用域内对图层的任意修改只会在结束时产生一条历史记录；
    /// 嵌套调用只在最外层记录。
    /// </summary>
    public IDisposable BeginEdit(string label)
    {
        if (_editDepth++ == 0)
        {
            foreach (var entry in _redo)
                entry.Dispose();
            _redo.Clear();

            _undo.Add(Capture(label));
            TrimHistory();
            IsDirty = true;
        }

        return new EditScope(this);
    }

    /// <summary>不产生历史记录的轻量修改（例如拖动不透明度滑块）。</summary>
    public void Touch()
    {
        IsDirty = true;
        Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class EditScope(ImageDocument document) : IDisposable
    {
        private ImageDocument? _document = document;

        public void Dispose()
        {
            var doc = _document;
            if (doc is null)
                return;

            _document = null;
            if (--doc._editDepth > 0)
                return;

            doc.Invalidate();
            doc.Changed?.Invoke(doc, EventArgs.Empty);
        }
    }

    // ------------------------------------------------------------------ 历史

    public void Undo()
    {
        if (_undo.Count == 0)
            return;

        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(Capture(entry.Label));

        foreach (var layer in _layers)
            layer.Dispose();
        _layers.Clear();
        _layers.AddRange(entry.Layers);
        entry.ReleaseLayers();

        Width = entry.Width;
        Height = entry.Height;
        ActiveIndex = entry.ActiveIndex;
        IsDirty = true;
        Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (_redo.Count == 0)
            return;

        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(Capture(entry.Label));

        foreach (var layer in _layers)
            layer.Dispose();
        _layers.Clear();
        _layers.AddRange(entry.Layers);
        entry.ReleaseLayers();

        Width = entry.Width;
        Height = entry.Height;
        ActiveIndex = entry.ActiveIndex;
        IsDirty = true;
        Invalidate();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearHistory()
    {
        foreach (var entry in _undo)
            entry.Dispose();
        foreach (var entry in _redo)
            entry.Dispose();
        _undo.Clear();
        _redo.Clear();
    }

    /// <summary>当前状态是否还能撤销（供 UI 判断）。</summary>
    public int HistoryDepth => _undo.Count;

    private Snapshot Capture(string label)
    {
        var clones = new List<Layer>(_layers.Count);
        foreach (var layer in _layers)
            clones.Add(layer.Clone());
        return new Snapshot(label, clones, ActiveIndex, Width, Height);
    }

    private void TrimHistory()
    {
        long total = 0;
        foreach (var entry in _undo)
            total += entry.Bytes;

        while (_undo.Count > 1 && (_undo.Count > MaxHistory || total > MaxHistoryBytes))
        {
            total -= _undo[0].Bytes;
            _undo[0].Dispose();
            _undo.RemoveAt(0);
        }
    }

    // ------------------------------------------------------------------ 图层操作

    public Layer AddLayer(string name, bool transparent = true)
    {
        using var _ = BeginEdit("新建图层");

        var layer = transparent
            ? Layer.CreateEmpty(Width, Height, name)
            : Layer.CreateFilled(Width, Height, name, new Bgra32 { R = 255, G = 255, B = 255, A = 255 });

        var index = Math.Clamp(_activeIndex + 1, 0, _layers.Count);
        _layers.Insert(index, layer);
        _activeIndex = index;
        return layer;
    }

    public void DuplicateLayer(int index)
    {
        var source = LayerAt(index);
        if (source is null)
            return;

        using var _ = BeginEdit("复制图层");
        var copy = source.CloneAs(source.Name + " 副本");
        _layers.Insert(index + 1, copy);
        _activeIndex = index + 1;
    }

    /// <summary>用给定的像素新建图层（“通过拷贝的图层”）。像素所有权转移给文档。</summary>
    public Layer AddLayerFromPixels(string name, Image<Bgra32> pixels)
    {
        using var _ = BeginEdit("通过拷贝的图层");

        var layer = new Layer(name, pixels);
        var index = Math.Clamp(_activeIndex + 1, 0, _layers.Count);
        _layers.Insert(index, layer);
        _activeIndex = index;
        return layer;
    }

    /// <summary>用选区覆盖度作为当前图层的蒙版（白色 = 选区内部）。</summary>
    public void SetMaskFromSelection(int index, Selection selection)
    {
        var layer = LayerAt(index);
        if (layer is null || selection.Width != layer.Width || selection.Height != layer.Height)
            return;

        using var _ = BeginEdit("由选区生成蒙版");
        layer.Mask?.Dispose();
        layer.Mask = selection.ToMask();
        layer.MaskEnabled = true;
    }

    /// <summary>
    /// 平移某个图层的内容（有选区时只移动选区内的像素）。返回是否真的发生了变化。
    /// 蒙版跟随内容一起移动，避免“内容走了、遮罩留在原地”。
    /// </summary>
    public bool MoveLayerContent(int index, int dx, int dy, Selection? clip, string label)
    {
        var layer = LayerAt(index);
        if (layer is null || (dx == 0 && dy == 0))
            return false;

        using var _ = BeginEdit(label);

        layer.ReplacePixels(MoveOps.Translate(layer.Pixels, dx, dy, clip));
        if (layer.Mask is { } mask)
            layer.ReplaceMask(MoveOps.TranslateMask(mask, dx, dy, clip));

        return true;
    }

    /// <summary>
    /// 按自由变换的几何参数重采样某个图层（有选区时只变换选区内的像素）。
    /// 与原样一致时不产生历史记录，返回 false。
    /// </summary>
    public bool TransformLayerContent(int index, in TransformGeometry geometry, Selection? clip, string label)
    {
        var layer = LayerAt(index);
        if (layer is null || geometry.IsIdentity)
            return false;

        using var _ = BeginEdit(label);

        layer.ReplacePixels(TransformOps.Warp(layer.Pixels, geometry, clip));
        if (layer.Mask is { } mask)
            layer.ReplaceMask(TransformOps.WarpMask(mask, geometry, clip));

        return true;
    }

    public void DeleteLayer(int index)
    {
        if (_layers.Count <= 1)
            return;

        var layer = LayerAt(index);
        if (layer is null)
            return;

        using var _ = BeginEdit("删除图层");
        _layers.RemoveAt(index);
        layer.Dispose();
        ActiveIndex = Math.Min(index, _layers.Count - 1);
    }

    /// <summary>把 <paramref name="from"/> 层移动到 <paramref name="to"/> 位置。</summary>
    public void MoveLayer(int from, int to)
    {
        if (from == to || from < 0 || from >= _layers.Count)
            return;

        to = Math.Clamp(to, 0, _layers.Count - 1);
        if (from == to)
            return;

        using var _ = BeginEdit("调整图层顺序");
        var layer = _layers[from];
        _layers.RemoveAt(from);
        _layers.Insert(to, layer);
        _activeIndex = to;
    }

    public bool CanMergeDown(int index) => index > 0 && index < _layers.Count;

    public void MergeDown(int index)
    {
        if (!CanMergeDown(index))
            return;

        using var _ = BeginEdit("向下合并");

        var upper = _layers[index];
        var lower = _layers[index - 1];

        // 只合成这两层（背景为空），合并结果已包含各自的混合方式与不透明度
        using var merged = LayerCompositor.Composite(Width, Height, [lower, upper]);
        lower.ReplacePixels(merged.Clone());
        lower.Opacity = 1;
        lower.BlendMode = BlendMode.Normal;
        lower.Mask?.Dispose();
        lower.Mask = null;
        lower.MaskEnabled = true;

        _layers.RemoveAt(index);
        upper.Dispose();
        _activeIndex = index - 1;
    }

    public void Flatten()
    {
        using var _ = BeginEdit("拼合图像");

        var flat = LayerCompositor.Composite(Width, Height, _layers);
        foreach (var layer in _layers)
            layer.Dispose();
        _layers.Clear();
        _layers.Add(new Layer("背景", flat));
        _activeIndex = 0;
    }

    /// <summary>由图层自身亮度生成蒙版（越亮的像素越可见）。</summary>
    public void AddMaskFromLuminance(int index)
    {
        var layer = LayerAt(index);
        if (layer is null || layer.HasMask)
            return;

        using var _ = BeginEdit("添加蒙版");

        var width = layer.Width;
        var height = layer.Height;
        var buffer = new byte[width * height];

        layer.Pixels.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var offset = y * width;
                for (var x = 0; x < width; x++)
                {
                    var p = row[x];
                    var luma = 0.299 * p.R + 0.587 * p.G + 0.114 * p.B;
                    // 兼顾透明度：半透明像素对应半透明白
                    buffer[offset + x] = (byte)Math.Clamp(Math.Round(luma * p.A / 255.0), 0, 255);
                }
            }
        });

        layer.Mask = Image.LoadPixelData<L8>(buffer, width, height);
        layer.MaskEnabled = true;
    }

    /// <summary>添加全白蒙版（完全显示），之后可用画笔擦除。</summary>
    public void AddRevealMask(int index)
    {
        var layer = LayerAt(index);
        if (layer is null || layer.HasMask)
            return;

        using var _ = BeginEdit("添加蒙版");
        layer.Mask = Layer.CreateMask(layer.Width, layer.Height);
        layer.MaskEnabled = true;
    }

    public void RemoveMask(int index)
    {
        var layer = LayerAt(index);
        if (layer?.Mask is null)
            return;

        using var _ = BeginEdit("删除蒙版");
        layer.Mask.Dispose();
        layer.Mask = null;
    }

    public void SetMaskEnabled(int index, bool enabled)
    {
        var layer = LayerAt(index);
        if (layer?.Mask is null || layer.MaskEnabled == enabled)
            return;

        layer.MaskEnabled = enabled;
        Touch();
    }

    // ------------------------------------------------------------------ 几何变换

    /// <summary>对全部图层的像素与蒙版施加同一变换（用于裁剪 / 旋转 / 缩放画布）。</summary>
    public void TransformAll(
        string label,
        Func<Image<Bgra32>, Image<Bgra32>> pixelOp,
        Func<Image<L8>, Image<L8>>? maskOp)
    {
        using var _ = BeginEdit(label);

        foreach (var layer in _layers)
            layer.Transform(pixelOp, maskOp);

        if (_layers.Count > 0)
        {
            Width = _layers[0].Width;
            Height = _layers[0].Height;
        }
    }

    public void Dispose()
    {
        ClearHistory();
        foreach (var layer in _layers)
            layer.Dispose();
        _layers.Clear();
        Invalidate();
    }

    // ------------------------------------------------------------------ 快照

    private sealed class Snapshot(string label, List<Layer> layers, int activeIndex, int width, int height)
    {
        private bool _released;

        public string Label { get; } = label;

        public List<Layer> Layers { get; } = layers;

        public int ActiveIndex { get; } = activeIndex;

        public int Width { get; } = width;

        public int Height { get; } = height;

        public long Bytes
        {
            get
            {
                long total = 0;
                foreach (var layer in Layers)
                {
                    total += (long)layer.Width * layer.Height * 4;
                    if (layer.Mask is not null)
                        total += (long)layer.Width * layer.Height;
                }
                return total;
            }
        }

        /// <summary>把图层所有权交给文档，之后本快照不再负责释放。</summary>
        public void ReleaseLayers()
        {
            Layers.Clear();
            _released = true;
        }

        public void Dispose()
        {
            if (_released)
                return;

            foreach (var layer in Layers)
                layer.Dispose();
            Layers.Clear();
            _released = true;
        }
    }
}
