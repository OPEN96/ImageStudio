using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageStudio.Models;

/// <summary>自由变换框上的操作点。</summary>
public enum TransformHandle
{
    None,
    Move,
    TopLeft,
    Top,
    TopRight,
    Right,
    BottomRight,
    Bottom,
    BottomLeft,
    Left,
    RotateTopLeft,
    RotateTopRight,
    RotateBottomRight,
    RotateBottomLeft
}

/// <summary>
/// 自由变换的几何描述：把源包围盒以自身中心为枢轴先缩放、再旋转，最后平移到目标中心。
/// 角度用弧度，纯数据，不依赖任何 UI 类型，方便直接测试。
/// </summary>
public struct TransformGeometry
{
    /// <summary>单边最小可视尺寸的一半，避免拖成零面积后退化。</summary>
    public const double MinExtent = 0.5;

    /// <summary>源包围盒的半宽。</summary>
    public double HalfWidth { get; set; }

    /// <summary>源包围盒的半高。</summary>
    public double HalfHeight { get; set; }

    /// <summary>源包围盒中心 X（枢轴，源坐标）。</summary>
    public double PivotX { get; set; }

    /// <summary>源包围盒中心 Y（枢轴，源坐标）。</summary>
    public double PivotY { get; set; }

    /// <summary>变换后中心 X（目标坐标）。</summary>
    public double CenterX { get; set; }

    /// <summary>变换后中心 Y（目标坐标）。</summary>
    public double CenterY { get; set; }

    public double ScaleX { get; set; }

    public double ScaleY { get; set; }

    /// <summary>旋转角（弧度，顺时针为正）。</summary>
    public double Rotation { get; set; }

    /// <summary>由画布上的一个矩形建立初始（未变换）状态。</summary>
    public static TransformGeometry ForBounds(double x, double y, double width, double height)
    {
        var w = Math.Max(width, MinExtent * 2);
        var h = Math.Max(height, MinExtent * 2);
        return new TransformGeometry
        {
            HalfWidth = w / 2,
            HalfHeight = h / 2,
            PivotX = x + w / 2,
            PivotY = y + h / 2,
            CenterX = x + w / 2,
            CenterY = y + h / 2,
            ScaleX = 1,
            ScaleY = 1,
            Rotation = 0
        };
    }

    /// <summary>源坐标 → 目标坐标的仿射矩阵。</summary>
    public readonly Matrix3x2 Forward =>
        Matrix3x2.CreateTranslation((float)-PivotX, (float)-PivotY)
        * Matrix3x2.CreateScale(Safe(ScaleX), Safe(ScaleY))
        * Matrix3x2.CreateRotation((float)Rotation)
        * Matrix3x2.CreateTranslation((float)CenterX, (float)CenterY);

    /// <summary>目标坐标 → 源坐标（重采样时按目标像素反查源像素）。</summary>
    public readonly Matrix3x2 Inverse
    {
        get
        {
            Matrix3x2.Invert(Forward, out var inverse);
            return inverse;
        }
    }

    /// <summary>是否与原样一致（可以跳过提交）。</summary>
    public readonly bool IsIdentity =>
        Math.Abs(CenterX - PivotX) < 0.01 &&
        Math.Abs(CenterY - PivotY) < 0.01 &&
        Math.Abs(ScaleX - 1) < 1e-4 &&
        Math.Abs(ScaleY - 1) < 1e-4 &&
        Math.Abs(Rotation) < 1e-6;

    /// <summary>目标空间中的四角，顺序为 左上、右上、右下、左下。</summary>
    public readonly Vector2[] Corners()
    {
        var ex = (float)(HalfWidth * ScaleX);
        var ey = (float)(HalfHeight * ScaleY);
        var cx = (float)CenterX;
        var cy = (float)CenterY;
        var cos = (float)Math.Cos(Rotation);
        var sin = (float)Math.Sin(Rotation);

        Vector2 At(float x, float y) => new(cx + x * cos - y * sin, cy + x * sin + y * cos);

        return [At(-ex, -ey), At(ex, -ey), At(ex, ey), At(-ex, ey)];
    }

    /// <summary>变换后内容的外接矩形（裁剪迭代范围用）。</summary>
    public readonly (double MinX, double MinY, double MaxX, double MaxY) TransformedBounds()
    {
        var corners = Corners();
        var minX = double.MaxValue;
        var minY = double.MaxValue;
        var maxX = double.MinValue;
        var maxY = double.MinValue;

        foreach (var corner in corners)
        {
            minX = Math.Min(minX, corner.X);
            minY = Math.Min(minY, corner.Y);
            maxX = Math.Max(maxX, corner.X);
            maxY = Math.Max(maxY, corner.Y);
        }

        return (minX, minY, maxX, maxY);
    }

    private static float Safe(double value) =>
        (float)(Math.Abs(value) < 1e-4 ? (value < 0 ? -1e-4 : 1e-4) : value);
}

/// <summary>把鼠标位置换算成新的变换参数。所有函数都是纯函数：从拖拽起始状态重算，不会累积误差。</summary>
public static class TransformMath
{
    /// <summary>整体平移。</summary>
    public static TransformGeometry Translate(in TransformGeometry baseline, Vector2 delta)
    {
        var geometry = baseline;
        geometry.CenterX += delta.X;
        geometry.CenterY += delta.Y;
        return geometry;
    }

    /// <summary>
    /// 拖动角点 / 边中点缩放。<paramref name="uniform"/> 为等比（Shift），
    /// <paramref name="fromCenter"/> 为围绕中心缩放（Alt），否则对角 / 对边固定。
    /// 拖过锚点会得到负缩放（即镜像），与 PS 行为一致。
    /// </summary>
    public static TransformGeometry Resize(
        in TransformGeometry baseline,
        TransformHandle handle,
        Vector2 pointer,
        bool uniform,
        bool fromCenter)
    {
        var (ix, iy) = Signs(handle);
        if (ix == 0 && iy == 0)
            return baseline;

        var geometry = baseline;
        var ex = geometry.HalfWidth * geometry.ScaleX;
        var ey = geometry.HalfHeight * geometry.ScaleY;

        var rotation = (float)geometry.Rotation;
        var center = new Vector2((float)geometry.CenterX, (float)geometry.CenterY);

        // 固定不动的参照点：默认是对角 / 对边，Alt 时是中心
        var anchor = fromCenter
            ? center
            : center + Rotate(new Vector2((float)(-ix * ex), (float)(-iy * ey)), rotation);

        var local = Rotate(pointer - anchor, -rotation);

        var newEx = ex;
        var newEy = ey;

        if (ix != 0)
            newEx = fromCenter ? ix * local.X : ix * local.X / 2;

        if (iy != 0)
            newEy = fromCenter ? iy * local.Y : iy * local.Y / 2;

        if (uniform && ix != 0 && iy != 0)
        {
            var kx = Math.Abs(newEx) / Math.Max(Math.Abs(ex), 1e-6);
            var ky = Math.Abs(newEy) / Math.Max(Math.Abs(ey), 1e-6);
            var k = Math.Max(kx, ky);
            newEx = (newEx < 0 ? -1 : 1) * Math.Abs(ex) * k;
            newEy = (newEy < 0 ? -1 : 1) * Math.Abs(ey) * k;
        }

        newEx = ClampExtent(newEx);
        newEy = ClampExtent(newEy);

        geometry.ScaleX = newEx / geometry.HalfWidth;
        geometry.ScaleY = newEy / geometry.HalfHeight;

        if (!fromCenter)
        {
            var moved = anchor + Rotate(new Vector2((float)(ix * newEx), (float)(iy * newEy)), rotation);
            geometry.CenterX = moved.X;
            geometry.CenterY = moved.Y;
        }

        return geometry;
    }

    /// <summary>绕中心旋转（<paramref name="snap"/> 为 Shift，按 15° 吸附）。</summary>
    public static TransformGeometry RotateTo(in TransformGeometry baseline, Vector2 from, Vector2 to, bool snap)
    {
        var center = new Vector2((float)baseline.CenterX, (float)baseline.CenterY);
        var start = Math.Atan2(from.Y - center.Y, from.X - center.X);
        var current = Math.Atan2(to.Y - center.Y, to.X - center.X);

        var angle = baseline.Rotation + (current - start);

        if (snap)
        {
            const double step = Math.PI / 12;
            angle = Math.Round(angle / step) * step;
        }

        var geometry = baseline;
        geometry.Rotation = angle;
        return geometry;
    }

    private static (int X, int Y) Signs(TransformHandle handle) => handle switch
    {
        TransformHandle.TopLeft => (-1, -1),
        TransformHandle.Top => (0, -1),
        TransformHandle.TopRight => (1, -1),
        TransformHandle.Right => (1, 0),
        TransformHandle.BottomRight => (1, 1),
        TransformHandle.Bottom => (0, 1),
        TransformHandle.BottomLeft => (-1, 1),
        TransformHandle.Left => (-1, 0),
        _ => (0, 0)
    };

    private static Vector2 Rotate(Vector2 value, float radians)
    {
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new Vector2(value.X * cos - value.Y * sin, value.X * sin + value.Y * cos);
    }

    private static double ClampExtent(double value) =>
        Math.Abs(value) < TransformGeometry.MinExtent
            ? (value < 0 ? -TransformGeometry.MinExtent : TransformGeometry.MinExtent)
            : value;
}

/// <summary>
/// 双线性重采样的自由变换。与 <see cref="MoveOps"/> 保持同一套语义：
/// 没有选区时整层变换，有选区时只搬选区内的像素（原位按覆盖度减淡，目标按覆盖度叠回）。
/// </summary>
public static class TransformOps
{
    /// <summary>按 <paramref name="geometry"/> 把 <paramref name="source"/> 变换到 <paramref name="destination"/>。</summary>
    public static void Warp(
        ReadOnlySpan<Bgra32> source,
        Span<Bgra32> destination,
        int width,
        int height,
        in TransformGeometry geometry,
        ReadOnlySpan<byte> coverage = default)
    {
        if (width <= 0 || height <= 0)
            return;

        var count = width * height;
        if (source.Length < count || destination.Length < count)
            throw new ArgumentException("缓冲尺寸小于画布尺寸。", nameof(destination));

        var clipped = coverage.Length >= count;
        var (x0, y0, x1, y1) = IterateRange(geometry, width, height);

        if (x1 <= x0 || y1 <= y0)
        {
            if (clipped)
                source[..count].CopyTo(destination);
            else
                destination[..count].Clear();
            return;
        }

        var inverse = geometry.Inverse;

        if (!clipped)
        {
            destination[..count].Clear();

            for (var y = y0; y < y1; y++)
            {
                var vy = y + 0.5f;
                var row = y * width;

                for (var x = x0; x < x1; x++)
                {
                    var point = Vector2.Transform(new Vector2(x + 0.5f, vy), inverse);
                    destination[row + x] = Sample(source, width, height, point);
                }
            }

            return;
        }

        // 有选区：两遍处理，避免原位置与新位置重叠时结果出错
        source[..count].CopyTo(destination);

        for (var index = 0; index < count; index++)
        {
            var c = coverage[index];
            if (c == 0)
                continue;

            var pixel = source[index];
            if (pixel.A == 0)
                continue;

            destination[index] = new Bgra32(pixel.R, pixel.G, pixel.B, (byte)(pixel.A * (255 - c) / 255));
        }

        for (var y = y0; y < y1; y++)
        {
            var vy = y + 0.5f;
            var row = y * width;

            for (var x = x0; x < x1; x++)
            {
                var point = Vector2.Transform(new Vector2(x + 0.5f, vy), inverse);
                var pixel = Sample(source, width, height, point);
                if (pixel.A == 0)
                    continue;

                var cover = SampleCoverage(coverage, width, height, point);
                if (cover <= 0)
                    continue;

                destination[row + x] = Over(destination[row + x], pixel, pixel.A * cover / 255f);
            }
        }
    }

    /// <summary>蒙版随内容一起变换（选区外的蒙版保持原样，重叠处取较大值）。</summary>
    public static void WarpMask(
        ReadOnlySpan<L8> source,
        Span<L8> destination,
        int width,
        int height,
        in TransformGeometry geometry,
        ReadOnlySpan<byte> coverage = default)
    {
        if (width <= 0 || height <= 0)
            return;

        var count = width * height;
        if (source.Length < count || destination.Length < count)
            throw new ArgumentException("缓冲尺寸小于画布尺寸。", nameof(destination));

        var clipped = coverage.Length >= count;
        var (x0, y0, x1, y1) = IterateRange(geometry, width, height);

        if (x1 <= x0 || y1 <= y0)
        {
            if (clipped)
                source[..count].CopyTo(destination);
            else
                destination[..count].Clear();
            return;
        }

        var inverse = geometry.Inverse;

        if (!clipped)
        {
            destination[..count].Clear();

            for (var y = y0; y < y1; y++)
            {
                var vy = y + 0.5f;
                var row = y * width;

                for (var x = x0; x < x1; x++)
                {
                    var point = Vector2.Transform(new Vector2(x + 0.5f, vy), inverse);
                    var value = SampleMask(source, width, height, point);
                    destination[row + x] = new L8((byte)Math.Clamp((int)MathF.Round(value), 0, 255));
                }
            }

            return;
        }

        source[..count].CopyTo(destination);

        for (var index = 0; index < count; index++)
        {
            var c = coverage[index];
            if (c == 0)
                continue;

            var value = source[index].PackedValue;
            destination[index] = new L8((byte)(value * (255 - c) / 255));
        }

        for (var y = y0; y < y1; y++)
        {
            var vy = y + 0.5f;
            var row = y * width;

            for (var x = x0; x < x1; x++)
            {
                var point = Vector2.Transform(new Vector2(x + 0.5f, vy), inverse);
                var cover = SampleCoverage(coverage, width, height, point);
                if (cover <= 0)
                    continue;

                var moved = SampleMask(source, width, height, point) * cover / 255f;
                var current = destination[row + x].PackedValue;
                destination[row + x] = new L8((byte)Math.Clamp((int)MathF.Round(Math.Max(current, moved)), 0, 255));
            }
        }
    }

    // ------------------------------------------------------------------ 便捷封装（一次性分配，用于提交与测试）

    /// <summary>变换整幅图并返回新图（<paramref name="clip"/> 为空则整层变换）。</summary>
    public static Image<Bgra32> Warp(Image<Bgra32> source, in TransformGeometry geometry, Selection? clip = null)
    {
        var width = source.Width;
        var height = source.Height;
        var pixels = new Bgra32[width * height];
        source.CopyPixelDataTo(pixels);

        var result = new Bgra32[width * height];
        Warp(pixels, result, width, height, geometry, CoverageOf(clip, width, height));
        return Image.LoadPixelData<Bgra32>(result, width, height);
    }

    /// <summary>变换蒙版并返回新蒙版。</summary>
    public static Image<L8> WarpMask(Image<L8> source, in TransformGeometry geometry, Selection? clip = null)
    {
        var width = source.Width;
        var height = source.Height;
        var values = new L8[width * height];
        source.CopyPixelDataTo(values);

        var result = new L8[width * height];
        WarpMask(values, result, width, height, geometry, CoverageOf(clip, width, height));
        return Image.LoadPixelData<L8>(result, width, height);
    }

    /// <summary>不透明内容的包围盒（用于确定变换框的初始位置）。全透明时退回整幅画布。</summary>
    public static Rectangle ContentBounds(Image<Bgra32> source)
    {
        var width = source.Width;
        var height = source.Height;

        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;

        source.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    if (row[x].A == 0)
                        continue;

                    if (x < minX)
                        minX = x;
                    if (x > maxX)
                        maxX = x;
                    if (y < minY)
                        minY = y;
                    if (y > maxY)
                        maxY = y;
                }
            }
        });

        if (maxX < minX || maxY < minY)
            return new Rectangle(0, 0, width, height);

        return new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    // ------------------------------------------------------------------ 采样

    private static (int X0, int Y0, int X1, int Y1) IterateRange(
        in TransformGeometry geometry, int width, int height)
    {
        var bounds = geometry.TransformedBounds();
        var x0 = Math.Max(0, (int)Math.Floor(bounds.MinX) - 1);
        var y0 = Math.Max(0, (int)Math.Floor(bounds.MinY) - 1);
        var x1 = Math.Min(width, (int)Math.Ceiling(bounds.MaxX) + 1);
        var y1 = Math.Min(height, (int)Math.Ceiling(bounds.MaxY) + 1);
        return (x0, y0, x1, y1);
    }

    private static Bgra32 Sample(ReadOnlySpan<Bgra32> source, int width, int height, Vector2 point)
    {
        var fx = point.X - 0.5f;
        var fy = point.Y - 0.5f;
        var ix = (int)MathF.Floor(fx);
        var iy = (int)MathF.Floor(fy);
        var tx = fx - ix;
        var ty = fy - iy;

        float weightA = 0, sumR = 0, sumG = 0, sumB = 0;

        for (var j = 0; j < 2; j++)
        {
            var y = iy + j;
            if (y < 0 || y >= height)
                continue;

            var wy = j == 0 ? 1 - ty : ty;
            if (wy <= 0)
                continue;

            var row = y * width;

            for (var i = 0; i < 2; i++)
            {
                var x = ix + i;
                if (x < 0 || x >= width)
                    continue;

                var wx = i == 0 ? 1 - tx : tx;
                var weight = wx * wy;
                if (weight <= 0)
                    continue;

                var pixel = source[row + x];
                var alpha = pixel.A * weight;
                weightA += alpha;
                sumR += pixel.R * alpha;
                sumG += pixel.G * alpha;
                sumB += pixel.B * alpha;
            }
        }

        if (weightA <= 0.5f)
            return default;

        // 按 alpha 加权即“预乘采样”，边缘不会出现黑边
        return new Bgra32(
            (byte)Math.Clamp((int)MathF.Round(sumR / weightA), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(sumG / weightA), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(sumB / weightA), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(weightA), 0, 255));
    }

    private static float SampleMask(ReadOnlySpan<L8> source, int width, int height, Vector2 point)
    {
        var fx = point.X - 0.5f;
        var fy = point.Y - 0.5f;
        var ix = (int)MathF.Floor(fx);
        var iy = (int)MathF.Floor(fy);
        var tx = fx - ix;
        var ty = fy - iy;

        float sum = 0, total = 0;

        for (var j = 0; j < 2; j++)
        {
            var y = iy + j;
            if (y < 0 || y >= height)
                continue;

            var wy = j == 0 ? 1 - ty : ty;
            if (wy <= 0)
                continue;

            var row = y * width;

            for (var i = 0; i < 2; i++)
            {
                var x = ix + i;
                if (x < 0 || x >= width)
                    continue;

                var wx = i == 0 ? 1 - tx : tx;
                var weight = wx * wy;
                if (weight <= 0)
                    continue;

                sum += source[row + x].PackedValue * weight;
                total += weight;
            }
        }

        return total <= 0 ? 0 : sum / total;
    }

    private static float SampleCoverage(ReadOnlySpan<byte> coverage, int width, int height, Vector2 point)
    {
        var fx = point.X - 0.5f;
        var fy = point.Y - 0.5f;
        var ix = (int)MathF.Floor(fx);
        var iy = (int)MathF.Floor(fy);
        var tx = fx - ix;
        var ty = fy - iy;

        float sum = 0, total = 0;

        for (var j = 0; j < 2; j++)
        {
            var y = iy + j;
            if (y < 0 || y >= height)
                continue;

            var wy = j == 0 ? 1 - ty : ty;
            if (wy <= 0)
                continue;

            var row = y * width;

            for (var i = 0; i < 2; i++)
            {
                var x = ix + i;
                if (x < 0 || x >= width)
                    continue;

                var wx = i == 0 ? 1 - tx : tx;
                var weight = wx * wy;
                if (weight <= 0)
                    continue;

                sum += coverage[row + x] * weight;
                total += weight;
            }
        }

        return total <= 0 ? 0 : sum / total;
    }

    /// <summary>直通 alpha 的 source-over，<paramref name="alpha"/> 取 0..255。</summary>
    private static Bgra32 Over(Bgra32 destination, Bgra32 source, float alpha)
    {
        var sa = Math.Clamp(alpha, 0, 255);
        if (sa <= 0)
            return destination;

        var da = destination.A;
        var back = da * (255 - sa) / 255f;
        var outA = sa + back;
        if (outA <= 0)
            return default;

        return new Bgra32(
            (byte)Math.Clamp((int)MathF.Round((source.R * sa + destination.R * back) / outA), 0, 255),
            (byte)Math.Clamp((int)MathF.Round((source.G * sa + destination.G * back) / outA), 0, 255),
            (byte)Math.Clamp((int)MathF.Round((source.B * sa + destination.B * back) / outA), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(outA), 0, 255));
    }

    private static ReadOnlySpan<byte> CoverageOf(Selection? clip, int width, int height) =>
        clip is { IsEmpty: false } && clip.Width == width && clip.Height == height
            ? clip.Coverage
            : default;
}
