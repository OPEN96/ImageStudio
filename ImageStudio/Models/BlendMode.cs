namespace ImageStudio.Models;

/// <summary>图层混合模式，命名与 Photoshop 保持一致。</summary>
public enum BlendMode
{
    Normal,
    Darken,
    Multiply,
    ColorBurn,
    Lighten,
    Screen,
    ColorDodge,
    Overlay,
    SoftLight,
    HardLight,
    Difference,
    Exclusion,
    Hue,
    Saturation,
    Color,
    Luminosity
}

/// <summary>
/// 混合模式的数学实现，公式取自 W3C Compositing and Blending Level 1。
/// 所有参数与返回值均为 0..1 的归一化颜色分量。
/// </summary>
public static class BlendModes
{
    public static readonly (BlendMode Mode, string Name)[] All =
    [
        (BlendMode.Normal, "正常"),
        (BlendMode.Darken, "变暗"),
        (BlendMode.Multiply, "正片叠底"),
        (BlendMode.ColorBurn, "颜色加深"),
        (BlendMode.Lighten, "变亮"),
        (BlendMode.Screen, "滤色"),
        (BlendMode.ColorDodge, "颜色减淡"),
        (BlendMode.Overlay, "叠加"),
        (BlendMode.SoftLight, "柔光"),
        (BlendMode.HardLight, "强光"),
        (BlendMode.Difference, "差值"),
        (BlendMode.Exclusion, "排除"),
        (BlendMode.Hue, "色相"),
        (BlendMode.Saturation, "饱和度"),
        (BlendMode.Color, "颜色"),
        (BlendMode.Luminosity, "明度")
    ];

    public static string NameOf(BlendMode mode)
    {
        foreach (var (m, name) in All)
            if (m == mode)
                return name;
        return "正常";
    }

    /// <summary>可分离混合模式（逐个颜色通道计算）返回闭包；不可分离的返回 null。</summary>
    public static Func<double, double, double>? Separable(BlendMode mode) => mode switch
    {
        BlendMode.Normal => static (_, cs) => cs,
        BlendMode.Darken => static (cb, cs) => Math.Min(cb, cs),
        BlendMode.Multiply => static (cb, cs) => cb * cs,
        BlendMode.ColorBurn => static (cb, cs) => cb >= 1 ? 1 : cs <= 0 ? 0 : 1 - Math.Min(1, (1 - cb) / cs),
        BlendMode.Lighten => static (cb, cs) => Math.Max(cb, cs),
        BlendMode.Screen => static (cb, cs) => cb + cs - cb * cs,
        BlendMode.ColorDodge => static (cb, cs) => cb <= 0 ? 0 : cs >= 1 ? 1 : Math.Min(1, cb / (1 - cs)),
        BlendMode.Overlay => static (cb, cs) => HardLight(cs, cb),
        BlendMode.SoftLight => SoftLight,
        BlendMode.HardLight => static (cb, cs) => HardLight(cb, cs),
        BlendMode.Difference => static (cb, cs) => Math.Abs(cb - cs),
        BlendMode.Exclusion => static (cb, cs) => cb + cs - 2 * cb * cs,
        _ => null
    };

    private static double HardLight(double cb, double cs) => cs <= 0.5
        ? cb * (2 * cs)
        : cb + (2 * cs - 1) - cb * (2 * cs - 1);

    private static double SoftLight(double cb, double cs)
    {
        if (cs <= 0.5)
            return cb - (1 - 2 * cs) * cb * (1 - cb);

        // D(cb)
        var d = cb <= 0.25 ? ((16 * cb - 12) * cb + 4) * cb : Math.Sqrt(cb);
        return cb + (2 * cs - 1) * (d - cb);
    }

    /// <summary>不可分离混合模式：需要同时考虑三个通道（色相 / 饱和度 / 颜色 / 明度）。</summary>
    public static void NonSeparable(
        BlendMode mode,
        double br, double bg, double bb,
        double sr, double sg, double sb,
        out double r, out double g, out double b)
    {
        double cbR = br, cbG = bg, cbB = bb;
        double csR = sr, csG = sg, csB = sb;

        switch (mode)
        {
            case BlendMode.Hue:
                SetSat(ref csR, ref csG, ref csB, Sat(cbR, cbG, cbB));
                SetLum(ref csR, ref csG, ref csB, Lum(cbR, cbG, cbB));
                (r, g, b) = (csR, csG, csB);
                break;

            case BlendMode.Saturation:
                SetSat(ref cbR, ref cbG, ref cbB, Sat(csR, csG, csB));
                SetLum(ref cbR, ref cbG, ref cbB, Lum(br, bg, bb));
                (r, g, b) = (cbR, cbG, cbB);
                break;

            case BlendMode.Color:
                SetLum(ref csR, ref csG, ref csB, Lum(cbR, cbG, cbB));
                (r, g, b) = (csR, csG, csB);
                break;

            default: // Luminosity
                SetLum(ref cbR, ref cbG, ref cbB, Lum(csR, csG, csB));
                (r, g, b) = (cbR, cbG, cbB);
                break;
        }
    }

    public static bool IsNonSeparable(BlendMode mode) =>
        mode is BlendMode.Hue or BlendMode.Saturation or BlendMode.Color or BlendMode.Luminosity;

    private static double Lum(double r, double g, double b) => 0.3 * r + 0.59 * g + 0.11 * b;

    private static double Sat(double r, double g, double b) =>
        Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));

    private static void SetLum(ref double r, ref double g, ref double b, double lum)
    {
        var d = lum - Lum(r, g, b);
        r += d;
        g += d;
        b += d;
        ClipColor(ref r, ref g, ref b);
    }

    private static void ClipColor(ref double r, ref double g, ref double b)
    {
        var l = Lum(r, g, b);
        var n = Math.Min(r, Math.Min(g, b));
        var x = Math.Max(r, Math.Max(g, b));

        if (n < 0 && l - n > 1e-9)
        {
            r = l + (r - l) * l / (l - n);
            g = l + (g - l) * l / (l - n);
            b = l + (b - l) * l / (l - n);
        }

        if (x > 1 && x - l > 1e-9)
        {
            r = l + (r - l) * (1 - l) / (x - l);
            g = l + (g - l) * (1 - l) / (x - l);
            b = l + (b - l) * (1 - l) / (x - l);
        }
    }

    private static void SetSat(ref double r, ref double g, ref double b, double sat)
    {
        // 按大小排序后重映射，最小的通道归零，最大的通道等于 sat
        var min = Math.Min(r, Math.Min(g, b));
        var max = Math.Max(r, Math.Max(g, b));
        if (max - min < 1e-9)
        {
            r = g = b = 0;
            return;
        }

        var scale = sat / (max - min);
        r = (r - min) * scale;
        g = (g - min) * scale;
        b = (b - min) * scale;
    }
}
