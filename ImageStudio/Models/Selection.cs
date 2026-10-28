using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageStudio.Models;

/// <summary>整数像素坐标（不依赖任何 UI 框架）。</summary>
public readonly record struct IntPoint(int X, int Y);

/// <summary>新选区与已有选区的合并方式，与 Photoshop 的四种模式一致。</summary>
public enum SelectionCombine
{
    Replace,
    Add,
    Subtract,
    Intersect
}

/// <summary>
/// 像素级选区：与画布等大的 8 位覆盖度图。
/// 0 表示完全未选中，255 表示完全选中，中间值来自羽化。
/// 实例构造后内部数据不再变化，所有变换都返回新实例。
/// </summary>
public sealed class Selection
{
    /// <summary>判定“在选区内”的覆盖度阈值，用于统计与轮廓。</summary>
    private const byte Inside = 128;

    private bool _statsValid;
    private bool _empty;
    private long _sum;
    private int _insideCount;
    private Rectangle _bounds;

    private bool _outlineBuilt;
    private List<IntPoint[]>? _outline;

    private Selection(int width, int height, byte[] coverage)
    {
        Width = width;
        Height = height;
        Coverage = coverage;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>长度为 Width * Height 的覆盖度数组（只读使用）。</summary>
    public byte[] Coverage { get; }

    public bool IsEmpty
    {
        get
        {
            EnsureStats();
            return _empty;
        }
    }

    /// <summary>选区外接矩形（覆盖度大于 0 的范围）。</summary>
    public Rectangle Bounds
    {
        get
        {
            EnsureStats();
            return _bounds;
        }
    }

    /// <summary>覆盖度不低于 50% 的像素数量。</summary>
    public int InsidePixelCount
    {
        get
        {
            EnsureStats();
            return _insideCount;
        }
    }

    /// <summary>平均覆盖度，0..1。</summary>
    public double CoverageRatio
    {
        get
        {
            EnsureStats();
            return (double)_sum / (255.0 * Width * Height);
        }
    }

    // ------------------------------------------------------------------ 构造

    public static Selection Empty(int width, int height) =>
        new(Math.Max(1, width), Math.Max(1, height), new byte[Math.Max(1, width) * Math.Max(1, height)]);

    public static Selection Full(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var data = new byte[width * height];
        Array.Fill(data, (byte)255);
        return new Selection(width, height, data);
    }

    public static Selection FromRect(int width, int height, Rectangle rect)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var data = new byte[width * height];

        rect = Rectangle.Intersect(rect, new Rectangle(0, 0, width, height));
        if (rect.Width > 0 && rect.Height > 0)
        {
            for (var y = rect.Y; y < rect.Bottom; y++)
                data.AsSpan(y * width + rect.X, rect.Width).Fill(255);
        }

        return new Selection(width, height, data);
    }

    public static Selection FromEllipse(int width, int height, Rectangle rect)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var data = new byte[width * height];

        rect = Rectangle.Intersect(rect, new Rectangle(0, 0, width, height));
        if (rect.Width <= 0 || rect.Height <= 0)
            return new Selection(width, height, data);

        var cx = rect.X + rect.Width / 2.0;
        var cy = rect.Y + rect.Height / 2.0;
        var rx = rect.Width / 2.0;
        var ry = rect.Height / 2.0;

        for (var y = rect.Y; y < rect.Bottom; y++)
        {
            var ny = (y + 0.5 - cy) / ry;
            var row = y * width;
            for (var x = rect.X; x < rect.Right; x++)
            {
                var nx = (x + 0.5 - cx) / rx;
                if (nx * nx + ny * ny <= 1.0)
                    data[row + x] = 255;
            }
        }

        return new Selection(width, height, data);
    }

    /// <summary>多边形填充（奇偶规则），用于套索。</summary>
    public static Selection FromPolygon(int width, int height, IReadOnlyList<(float X, float Y)> points)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var data = new byte[width * height];

        if (points.Count < 3)
            return new Selection(width, height, data);

        var xs = new List<double>(points.Count);
        var n = points.Count;

        for (var y = 0; y < height; y++)
        {
            var scan = y + 0.5;
            xs.Clear();

            for (var i = 0; i < n; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % n];

                // 半开区间判定，避免顶点被重复计入
                if (a.Y <= scan == b.Y <= scan)
                    continue;

                var t = (scan - a.Y) / (b.Y - a.Y);
                xs.Add(a.X + t * (b.X - a.X));
            }

            if (xs.Count < 2)
                continue;

            xs.Sort();
            var row = y * width;
            for (var i = 0; i + 1 < xs.Count; i += 2)
            {
                var start = (int)Math.Ceiling(xs[i] - 0.5);
                var end = (int)Math.Floor(xs[i + 1] - 0.5);
                start = Math.Max(0, start);
                end = Math.Min(width - 1, end);
                for (var x = start; x <= end; x++)
                    data[row + x] = 255;
            }
        }

        return new Selection(width, height, data);
    }

    /// <summary>魔棒：从种子点按颜色容差扩散（或全图匹配）。</summary>
    public static Selection Wand(
        Image<Bgra32> source, int seedX, int seedY, double tolerance, bool contiguous)
    {
        var w = source.Width;
        var h = source.Height;
        var data = new byte[w * h];

        if (seedX < 0 || seedY < 0 || seedX >= w || seedY >= h)
            return new Selection(w, h, data);

        var pixels = new Bgra32[w * h];
        source.CopyPixelDataTo(pixels);

        var seed = pixels[seedY * w + seedX];

        // 容差与 Photoshop 一致：0..255，映射到 RGBA 欧氏距离的最大值 510
        var limit = Math.Clamp(tolerance, 0, 255) * 2.0;
        var limitSq = limit * limit;

        bool Match(int index)
        {
            var p = pixels[index];
            int dr = p.R - seed.R;
            int dg = p.G - seed.G;
            int db = p.B - seed.B;
            int da = p.A - seed.A;
            return dr * dr + dg * dg + db * db + da * da <= limitSq;
        }

        if (!contiguous)
        {
            for (var i = 0; i < data.Length; i++)
            {
                if (Match(i))
                    data[i] = 255;
            }

            return new Selection(w, h, data);
        }

        var visited = new bool[data.Length];
        var stack = new Stack<int>();
        stack.Push(seedY * w + seedX);

        while (stack.Count > 0)
        {
            var index = stack.Pop();
            if (visited[index])
                continue;

            var y = index / w;
            var x = index - y * w;

            var left = x;
            while (left >= 0 && !visited[y * w + left] && Match(y * w + left))
                left--;
            left++;

            var right = x;
            while (right < w && !visited[y * w + right] && Match(y * w + right))
                right++;
            right--;

            if (left > right)
            {
                visited[index] = true;
                continue;
            }

            var row = y * w;
            for (var i = left; i <= right; i++)
            {
                visited[row + i] = true;
                data[row + i] = 255;
            }

            for (var i = left; i <= right; i++)
            {
                if (y > 0)
                {
                    var up = (y - 1) * w + i;
                    if (!visited[up] && Match(up))
                        stack.Push(up);
                }

                if (y < h - 1)
                {
                    var down = (y + 1) * w + i;
                    if (!visited[down] && Match(down))
                        stack.Push(down);
                }
            }
        }

        return new Selection(w, h, data);
    }

    public static Selection FromMask(Image<L8> mask)
    {
        var data = new byte[mask.Width * mask.Height];
        mask.CopyPixelDataTo(data);
        return new Selection(mask.Width, mask.Height, data);
    }

    public static Selection FromCoverage(int width, int height, byte[] coverage) =>
        new(width, height, coverage);

    public Image<L8> ToMask() => Image.LoadPixelData<L8>(Coverage, Width, Height);

    // ------------------------------------------------------------------ 组合与派生

    public Selection Combine(Selection other, SelectionCombine mode)
    {
        if (mode == SelectionCombine.Replace)
            return other;

        if (other.Width != Width || other.Height != Height)
            return this;

        var data = new byte[Coverage.Length];

        for (var i = 0; i < data.Length; i++)
        {
            int a = Coverage[i];
            int b = other.Coverage[i];

            data[i] = mode switch
            {
                SelectionCombine.Add => (byte)Math.Max(a, b),
                SelectionCombine.Subtract => (byte)Math.Max(0, a - a * b / 255),
                SelectionCombine.Intersect => (byte)(a * b / 255),
                _ => (byte)a
            };
        }

        return new Selection(Width, Height, data);
    }

    public Selection Invert()
    {
        var data = new byte[Coverage.Length];
        for (var i = 0; i < data.Length; i++)
            data[i] = (byte)(255 - Coverage[i]);
        return new Selection(Width, Height, data);
    }

    /// <summary>羽化：三次盒式模糊逼近高斯，半径单位像素。</summary>
    public Selection Feather(double radius)
    {
        if (radius <= 0.05)
            return this;

        // 两次盒式模糊：覆盖范围约等于羽化半径，中心区域保持完全不透明
        var r = Math.Max(1, (int)Math.Round(radius * 0.6));
        var data = (byte[])Coverage.Clone();

        for (var pass = 0; pass < 2; pass++)
            data = BoxBlur(data, Width, Height, r);

        return new Selection(Width, Height, data);
    }

    private static byte[] BoxBlur(byte[] source, int width, int height, int radius)
    {
        var horizontal = new byte[source.Length];
        var result = new byte[source.Length];
        var window = radius * 2 + 1;

        // 水平
        for (var y = 0; y < height; y++)
        {
            var row = y * width;
            var sum = 0;

            for (var i = -radius; i <= radius; i++)
                sum += source[row + Math.Clamp(i, 0, width - 1)];

            for (var x = 0; x < width; x++)
            {
                horizontal[row + x] = (byte)(sum / window);
                var add = source[row + Math.Clamp(x + radius + 1, 0, width - 1)];
                var remove = source[row + Math.Clamp(x - radius, 0, width - 1)];
                sum += add - remove;
            }
        }

        // 垂直
        for (var x = 0; x < width; x++)
        {
            var sum = 0;
            for (var i = -radius; i <= radius; i++)
                sum += horizontal[Math.Clamp(i, 0, height - 1) * width + x];

            for (var y = 0; y < height; y++)
            {
                result[y * width + x] = (byte)(sum / window);
                var add = horizontal[Math.Clamp(y + radius + 1, 0, height - 1) * width + x];
                var remove = horizontal[Math.Clamp(y - radius, 0, height - 1) * width + x];
                sum += add - remove;
            }
        }

        return result;
    }

    /// <summary>裁剪（画布尺寸变化时同步选区）。</summary>
    public Selection Crop(Rectangle rect)
    {
        var w = Math.Max(1, rect.Width);
        var h = Math.Max(1, rect.Height);
        var data = new byte[w * h];

        for (var y = 0; y < h; y++)
        {
            var sy = rect.Y + y;
            if (sy < 0 || sy >= Height)
                continue;

            for (var x = 0; x < w; x++)
            {
                var sx = rect.X + x;
                if (sx < 0 || sx >= Width)
                    continue;

                data[y * w + x] = Coverage[sy * Width + sx];
            }
        }

        return new Selection(w, h, data);
    }

    /// <summary>最近邻缩放（画布整体缩放时同步选区）。</summary>
    public Selection ScaleTo(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var data = new byte[width * height];

        for (var y = 0; y < height; y++)
        {
            var sy = (int)((long)y * Height / height);
            var srcRow = sy * Width;
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var sx = (int)((long)x * Width / width);
                data[row + x] = Coverage[srcRow + sx];
            }
        }

        return new Selection(width, height, data);
    }

    // ------------------------------------------------------------------ 轮廓（蚂蚁线）

    /// <summary>
    /// 返回选区边界折线（图像坐标，已合并共线点）。空选区或过于复杂时返回 null。
    /// 结果被缓存，可反复调用。
    /// </summary>
    public IReadOnlyList<IntPoint[]>? GetOutline()
    {
        if (_outlineBuilt)
            return _outline;

        _outlineBuilt = true;

        if (IsEmpty)
        {
            _outline = [];
            return _outline;
        }

        const int maxEdges = 800_000;
        var w = Width;
        var h = Height;
        var adjacency = new Dictionary<long, List<long>>();

        long Key(int x, int y) => (long)y * (w + 1) + x;

        bool AddEdge(long a, long b)
        {
            if (!adjacency.TryGetValue(a, out var la))
                adjacency[a] = la = new List<long>(4);
            la.Add(b);

            if (!adjacency.TryGetValue(b, out var lb))
                adjacency[b] = lb = new List<long>(4);
            lb.Add(a);

            return adjacency.Count <= maxEdges;
        }

        bool In(int x, int y) =>
            x >= 0 && y >= 0 && x < w && y < h && Coverage[y * w + x] >= Inside;

        var aborted = false;

        // 水平边：像素 (x,y-1) 与 (x,y) 之间
        for (var y = 0; y <= h && !aborted; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (In(x, y - 1) == In(x, y))
                    continue;

                if (!AddEdge(Key(x, y), Key(x + 1, y)))
                {
                    aborted = true;
                    break;
                }
            }
        }

        // 垂直边：像素 (x-1,y) 与 (x,y) 之间
        for (var x = 0; x <= w && !aborted; x++)
        {
            for (var y = 0; y < h; y++)
            {
                if (In(x - 1, y) == In(x, y))
                    continue;

                if (!AddEdge(Key(x, y), Key(x, y + 1)))
                {
                    aborted = true;
                    break;
                }
            }
        }

        if (aborted)
        {
            _outline = null;
            return null;
        }

        var used = new HashSet<(long, long)>();
        var loops = new List<IntPoint[]>();

        static (long, long) Pair(long a, long b) => a < b ? (a, b) : (b, a);

        foreach (var seed in adjacency.Keys)
        {
            while (true)
            {
                var start = -1L;
                foreach (var neighbour in adjacency[seed])
                {
                    if (used.Contains(Pair(seed, neighbour)))
                        continue;
                    start = neighbour;
                    break;
                }

                if (start < 0)
                    break;

                var path = new List<IntPoint>();
                var current = seed;
                var next = start;

                while (true)
                {
                    path.Add(new IntPoint((int)(current % (w + 1)), (int)(current / (w + 1))));
                    used.Add(Pair(current, next));
                    current = next;

                    if (current == seed)
                        break;

                    var following = -1L;
                    foreach (var neighbour in adjacency[current])
                    {
                        if (used.Contains(Pair(current, neighbour)))
                            continue;
                        following = neighbour;
                        break;
                    }

                    if (following < 0)
                        break;

                    next = following;
                }

                var simplified = Simplify(path);
                if (simplified.Length >= 2)
                    loops.Add(simplified);
            }
        }

        _outline = loops;
        return _outline;
    }

    /// <summary>去掉共线的中间点，只保留转折点。</summary>
    private static IntPoint[] Simplify(List<IntPoint> path)
    {
        if (path.Count < 3)
            return path.ToArray();

        var result = new List<IntPoint>(path.Count);

        for (var i = 0; i < path.Count; i++)
        {
            var previous = path[(i - 1 + path.Count) % path.Count];
            var current = path[i];
            var next = path[(i + 1) % path.Count];

            var d1x = Math.Sign(current.X - previous.X);
            var d1y = Math.Sign(current.Y - previous.Y);
            var d2x = Math.Sign(next.X - current.X);
            var d2y = Math.Sign(next.Y - current.Y);

            if (d1x != d2x || d1y != d2y)
                result.Add(current);
        }

        return result.ToArray();
    }

    private void EnsureStats()
    {
        if (_statsValid)
            return;

        _statsValid = true;

        var w = Width;
        var h = Height;
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = -1;
        var maxY = -1;
        long sum = 0;
        var inside = 0;

        for (var y = 0; y < h; y++)
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var v = Coverage[row + x];
                if (v == 0)
                    continue;

                sum += v;
                if (v >= Inside)
                    inside++;

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        _sum = sum;
        _insideCount = inside;

        if (maxX < 0)
        {
            _empty = true;
            _bounds = Rectangle.Empty;
        }
        else
        {
            _empty = false;
            _bounds = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        }
    }
}
