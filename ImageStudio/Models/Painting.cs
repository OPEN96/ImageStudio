using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ImageStudio.Models;

/// <summary>画笔参数，单位均为像素 / 比例，与 UI 框架无关。</summary>
public sealed class BrushSettings
{
    /// <summary>笔刷直径（像素）。</summary>
    public double Size { get; init; } = 24;

    /// <summary>硬度 0..1：1 为硬边，0 为完全柔和。</summary>
    public double Hardness { get; init; } = 0.8;

    /// <summary>整笔的不透明度 0..1（同一笔内叠加不会超过该值）。</summary>
    public double Opacity { get; init; } = 1;

    /// <summary>相邻两个笔迹的间距，取直径的比例，越小越平滑也越慢。</summary>
    public double Spacing { get; init; } = 0.12;

    /// <summary>true 表示擦除（降低透明度）而不是上色。</summary>
    public bool Erase { get; init; }
}

/// <summary>
/// 一次完整的笔画。构造时复制一份图层像素作为底稿，之后每走一步只在底稿上叠加覆盖率，
/// 因此同一笔内反复涂抹不会变得更深（与 Photoshop 的不透明度语义一致）。
/// 所有坐标使用图像像素坐标，允许落在画布外（会被裁掉）。
/// </summary>
public sealed class StrokeSession : IDisposable
{
    private const int MaxDabsPerMove = 8192;

    private readonly int _width;
    private readonly int _height;
    private readonly Bgra32[] _base;
    private readonly byte[] _coverage;
    private readonly byte[]? _clip;
    private readonly Image<Bgra32> _pixels;

    private readonly Bgra32 _color;
    private readonly double _radius;
    private readonly double _hardness;
    private readonly double _opacity;
    private readonly double _step;
    private readonly double _colorAlpha;
    private readonly bool _erase;

    private double? _lastX;
    private double? _lastY;
    private bool _ownsPixels = true;
    private bool _disposed;

    public StrokeSession(Image<Bgra32> source, BrushSettings brush, Bgra32 color, Selection? clip = null)
    {
        _width = source.Width;
        _height = source.Height;

        var length = _width * _height;
        _base = new Bgra32[length];
        source.CopyPixelDataTo(_base);

        _pixels = Image.LoadPixelData<Bgra32>((Bgra32[])_base.Clone(), _width, _height);

        _coverage = new byte[length];

        // 选区尺寸与画布不一致时忽略，避免越界
        _clip = clip is { IsEmpty: false } && clip.Width == _width && clip.Height == _height
            ? clip.Coverage
            : null;

        _color = color;
        _erase = brush.Erase;
        _radius = Math.Max(0.5, brush.Size / 2.0);
        _hardness = Math.Clamp(brush.Hardness, 0, 1);
        _opacity = Math.Clamp(brush.Opacity, 0, 1);
        _step = Math.Max(0.5, brush.Size * Math.Clamp(brush.Spacing, 0.01, 1));
        _colorAlpha = color.A / 255.0;
    }

    public int Width => _width;

    public int Height => _height;

    /// <summary>本笔画的实时结果，可由调用方读取（所有权仍归本对象）。</summary>
    public Image<Bgra32> Pixels => _pixels;

    /// <summary>本笔画是否已经落笔（用于判断是否需要提交）。</summary>
    public bool HasPaint { get; private set; }

    /// <summary>
    /// 把画笔移动到给定位置。首次调用直接落笔，之后按 <see cref="BrushSettings.Spacing"/>
    /// 沿直线插值补点，保证快速拖动时笔迹连续。
    /// </summary>
    public void MoveTo(double x, double y)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_lastX is not { } lastX || _lastY is not { } lastY)
        {
            Stamp(x, y);
            _lastX = x;
            _lastY = y;
            return;
        }

        var dx = x - lastX;
        var dy = y - lastY;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        if (distance < 1e-6)
            return;

        var dabs = (int)Math.Ceiling(distance / _step);
        dabs = Math.Clamp(dabs, 1, MaxDabsPerMove);

        for (var i = 1; i <= dabs; i++)
        {
            var t = (double)i / dabs;
            Stamp(lastX + dx * t, lastY + dy * t);
        }

        _lastX = x;
        _lastY = y;
    }

    /// <summary>交出像素缓冲的所有权（提交到图层时使用）。</summary>
    public Image<Bgra32> TakePixels()
    {
        _ownsPixels = false;
        return _pixels;
    }

    /// <summary>
    /// 单个笔迹在距离 <paramref name="distance"/> 处的覆盖率，0..1。
    /// 硬度决定实心半径，实心区外做平滑衰减，边缘天然带抗锯齿。
    /// </summary>
    public static double DabCoverage(double distance, double radius, double hardness)
    {
        if (radius <= 0)
            return 0;

        var inner = radius * Math.Clamp(hardness, 0, 1);

        // 保留至少 1px 的过渡带，硬度为 1 时也不会出现锯齿
        if (radius - inner < 1)
            inner = Math.Max(0, radius - 1);

        if (distance <= inner)
            return 1;

        if (distance >= radius)
            return 0;

        var t = (radius - distance) / (radius - inner);
        return t * t * (3 - 2 * t);
    }

    private void Stamp(double cx, double cy)
    {
        var radius = _radius;
        var x0 = Math.Max(0, (int)Math.Floor(cx - radius));
        var y0 = Math.Max(0, (int)Math.Floor(cy - radius));
        var x1 = Math.Min(_width - 1, (int)Math.Ceiling(cx + radius));
        var y1 = Math.Min(_height - 1, (int)Math.Ceiling(cy + radius));

        if (x0 > x1 || y0 > y1)
            return;

        var touched = false;

        // 逐行写入：笔画只覆盖一小片区域，不必复制整张图
        _pixels.ProcessPixelRows(accessor =>
        {
            for (var y = y0; y <= y1; y++)
            {
                var dy = y + 0.5 - cy;
                var row = y * _width;
                var span = accessor.GetRowSpan(y);

                for (var x = x0; x <= x1; x++)
                {
                    var dx = x + 0.5 - cx;
                    var coverage = DabCoverage(Math.Sqrt(dx * dx + dy * dy), radius, _hardness);
                    if (coverage <= 0)
                        continue;

                    var index = row + x;

                    if (_clip is not null)
                    {
                        var clipValue = _clip[index];
                        if (clipValue == 0)
                            continue;
                        coverage *= clipValue / 255.0;
                    }

                    var value = (byte)Math.Round(coverage * 255);
                    if (value <= _coverage[index])
                        continue;

                    _coverage[index] = value;
                    span[x] = Composite(_base[index], value);
                    touched = true;
                }
            }
        });

        if (touched)
            HasPaint = true;
    }

    /// <summary>把覆盖率折算为像素：上色做 source-over，擦除按比例降低原有透明度。</summary>
    private Bgra32 Composite(Bgra32 under, byte coverage)
    {
        var a = coverage / 255.0 * _opacity;
        if (a <= 0)
            return under;

        if (_erase)
        {
            var keep = 1 - a;
            return new Bgra32
            {
                R = under.R,
                G = under.G,
                B = under.B,
                A = (byte)Math.Round(under.A * keep)
            };
        }

        var sourceAlpha = a * _colorAlpha;
        if (sourceAlpha <= 0)
            return under;

        var underAlpha = under.A / 255.0;
        var outAlpha = sourceAlpha + underAlpha * (1 - sourceAlpha);
        if (outAlpha <= 0)
            return default;

        var k = 1.0 / outAlpha;
        var mix = underAlpha * (1 - sourceAlpha) * k;

        return new Bgra32
        {
            R = Clamp(_color.R * sourceAlpha * k + under.R * mix),
            G = Clamp(_color.G * sourceAlpha * k + under.G * mix),
            B = Clamp(_color.B * sourceAlpha * k + under.B * mix),
            A = Clamp(outAlpha * 255)
        };
    }

    private static byte Clamp(double v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)Math.Round(v);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_ownsPixels)
            _pixels.Dispose();
    }
}
