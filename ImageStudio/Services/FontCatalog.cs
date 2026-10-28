using Avalonia.Media;

namespace ImageStudio.Services;

/// <summary>
/// 系统字体目录。各平台的默认中文字体名称完全不同（Windows 微软雅黑、macOS 苹方、
/// Linux 思源/Noto），因此启动时把系统字体枚举一遍，按优先级挑一个真实存在的，
/// 再把它作为界面与文字工具的默认字体，避免在 Linux 上落到没有中文字形的字体上。
/// </summary>
public static class FontCatalog
{
    /// <summary>优先级从高到低：先挑各平台最标准的中文字体，再退到通用无衬线字体。</summary>
    private static readonly string[] Preferred =
    [
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "PingFang SC",
        "Hiragino Sans GB",
        "Noto Sans CJK SC",
        "Noto Sans SC",
        "Noto Sans CJK",
        "Source Han Sans SC",
        "Source Han Sans CN",
        "WenQuanYi Micro Hei",
        "WenQuanYi Zen Hei",
        "Droid Sans Fallback",
        "Segoe UI",
        "Noto Sans",
        "DejaVu Sans",
        "Liberation Sans",
        "Arial"
    ];

    /// <summary>界面与文字工具的兜底字体名（即使没做枚举也能用的安全值）。</summary>
    public const string FallbackFamily = "Microsoft YaHei UI, Segoe UI, Noto Sans CJK SC, DejaVu Sans";

    private static readonly object Gate = new();
    private static bool _loaded;
    private static string _defaultFamily = FallbackFamily;
    private static IReadOnlyList<string> _families = [];

    /// <summary>已解析的默认字体名。首次访问时自动枚举系统字体。</summary>
    public static string DefaultFamily
    {
        get
        {
            EnsureLoaded();
            return _defaultFamily;
        }
    }

    /// <summary>系统里可用的字体族名（按字母序）。</summary>
    public static IReadOnlyList<string> Families
    {
        get
        {
            EnsureLoaded();
            return _families;
        }
    }

    /// <summary>枚举系统字体（必须在 UI 线程、Avalonia 初始化之后调用）。重复调用只做一次。</summary>
    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded)
                return;

            _loaded = true;

            try
            {
                var names = new List<string>();
                foreach (var family in FontManager.Current.SystemFonts)
                {
                    var name = family.Name;
                    if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                        names.Add(name);
                }

                names.Sort(StringComparer.CurrentCultureIgnoreCase);
                _families = names;

                // 先按偏好挑；一个都命中就用系统默认字体（Avalonia 的 $Default）
                _defaultFamily = Preferred
                    .Select(candidate => names.FirstOrDefault(
                        name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)))
                    .FirstOrDefault(match => match is not null)
                    ?? FallbackFamily;
            }
            catch
            {
                // 设计时 / 无渲染后端的环境拿不到字体列表，保持兜底值
                _defaultFamily = FallbackFamily;
                _families = [];
            }
        }
    }
}
