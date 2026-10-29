using System.Windows;
using System.Windows.Media;
using ClipNest.Infrastructure.Diagnostics;
using ClipNest.Services;
using Microsoft.Win32;

namespace ClipNest.UI.Theming;

/// <summary>
/// 把明暗模式 + 主题色算成一组画刷，直接写进 Application.Resources。
///
/// XAML 里所有画刷都用 <c>DynamicResource</c> 引用，所以换主题时不需要重建窗口，
/// 也不用挨个控件去刷颜色。代价是这些 key 不能在 XAML 里定义，否则动态引用会先命中
/// 静态定义而看不到变化 —— 唯一的真相在 <see cref="BuildBrushes"/>。
/// </summary>
public sealed class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string LightAppsValue = "AppsUseLightTheme";

    private readonly Application _application;
    private ThemeMode _mode = ThemeMode.Light;

    public ThemeService(Application application) => _application = application;

    /// <summary>当前实际生效的是不是深色（跟随系统时由系统决定）。</summary>
    public bool IsDark { get; private set; }

    public void Apply(ThemeMode mode, string? accentKey)
    {
        _mode = mode;
        IsDark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.System => SystemPrefersDark(),
            _ => false,
        };

        var accented = AccentPalettes.Resolve(accentKey);
        CurrentAccentKey = accented.Key;

        foreach (var (key, brush) in BuildBrushes(IsDark, accented.BaseColor))
        {
            _application.Resources[key] = brush;
        }

        Log.Info($"主题已应用：模式={mode}（实际 {(IsDark ? "深色" : "浅色")}），主题色={accented.Key}");
    }

    /// <summary>
    /// 系统明暗变化时由隐藏窗口的 WM_SETTINGCHANGE 触发。
    /// 只有「跟随系统」才需要响应，否则用户手选的模式会被系统悄悄改掉。
    /// </summary>
    public void OnSystemPreferenceChanged()
    {
        if (_mode != ThemeMode.System) return;

        var dark = SystemPrefersDark();
        if (dark == IsDark) return;

        Log.Info($"系统主题变化，切换到{(dark ? "深色" : "浅色")}");
        Apply(ThemeMode.System, CurrentAccentKey);
    }

    private string CurrentAccentKey { get; set; } = AccentPalettes.DefaultKey;

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // AppsUseLightTheme: 1 = 应用使用浅色，0 = 深色；读不到就当浅色
            return key?.GetValue(LightAppsValue) is int value && value == 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"读取系统主题失败，按浅色处理：{ex.Message}");
            return false;
        }
    }

    private static IEnumerable<(string Key, SolidColorBrush Brush)> BuildBrushes(bool dark, Color accentBase)
    {
        var windowBg = dark ? Rgb(0x1A, 0x1B, 0x19) : Rgb(0xFA, 0xF9, 0xF7);
        var surface = dark ? Rgb(0x21, 0x22, 0x20) : Rgb(0xFF, 0xFF, 0xFF);
        var borderSoft = dark ? Rgb(0x32, 0x33, 0x2F) : Rgb(0xE7, 0xE4, 0xDF);
        var borderStrong = dark ? Rgb(0x44, 0x45, 0x3F) : Rgb(0xD8, 0xD4, 0xCD);
        var textPrimary = dark ? Rgb(0xED, 0xEB, 0xE6) : Rgb(0x1D, 0x1C, 0x1A);
        var textSecondary = dark ? Rgb(0xA8, 0xA4, 0x9C) : Rgb(0x6E, 0x6A, 0x63);
        var textMuted = dark ? Rgb(0x83, 0x7F, 0x76) : Rgb(0x9A, 0x95, 0x8C);
        var hover = dark ? Rgb(0x2A, 0x2B, 0x27) : Rgb(0xF2, 0xF0, 0xEC);
        var danger = dark ? Rgb(0xE2, 0x7A, 0x6A) : Rgb(0xB4, 0x44, 0x3A);

        // 深色下把主题色提亮，否则低饱和的深色在深底上对比不够，小字发灰
        var accent = dark ? Mix(accentBase, Colors.White, 0.28) : accentBase;
        var accentSoft = dark ? Mix(surface, accent, 0.20) : Mix(windowBg, accent, 0.12);
        var selected = dark ? Mix(surface, accent, 0.32) : Mix(windowBg, accent, 0.20);
        var onAccent = Luminance(accent) > 0.62 ? Rgb(0x1A, 0x1B, 0x19) : Colors.White;

        yield return ("WindowBg", Frozen(windowBg));
        yield return ("SurfaceBg", Frozen(surface));
        yield return ("BorderSoft", Frozen(borderSoft));
        yield return ("BorderStrong", Frozen(borderStrong));
        yield return ("TextPrimary", Frozen(textPrimary));
        yield return ("TextSecondary", Frozen(textSecondary));
        yield return ("TextMuted", Frozen(textMuted));
        yield return ("HoverBg", Frozen(hover));
        yield return ("Danger", Frozen(danger));
        yield return ("Accent", Frozen(accent));
        yield return ("AccentSoft", Frozen(accentSoft));
        yield return ("OnAccent", Frozen(onAccent));
        yield return ("SelectedBg", Frozen(selected));
        yield return ("ScrollThumb", Frozen(borderStrong));
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    /// <summary>把 <paramref name="amount"/> 比例的 <paramref name="to"/> 混进 <paramref name="from"/>。</summary>
    private static Color Mix(Color from, Color to, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    /// <summary>粗略相对亮度，只用来决定强调色上用黑字还是白字。</summary>
    private static double Luminance(Color color)
        => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
