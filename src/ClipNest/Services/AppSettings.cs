using System.Text.Json.Serialization;

namespace ClipNest.Services;

/// <summary>界面主题。跟随系统时会实时读取 Windows 的明暗设置。</summary>
public enum ThemeMode
{
    Light = 0,
    Dark = 1,
    System = 2,
}

public sealed class HotKeySettings
{
    public bool ToggleWindowEnabled { get; set; } = true;

    public string ToggleWindow { get; set; } = "Ctrl+Alt+V";

    public bool ManualCaptureEnabled { get; set; } = true;

    public string ManualCapture { get; set; } = "Alt+C";
}

/// <summary>settings.json 的形态。所有字段都要有安全默认值，文件损坏时能直接回退。</summary>
public sealed class AppSettings
{
    public const int MinItemsLimit = 100;
    public const int MaxItemsLimit = 1_000_000;

    /// <summary>总开关。</summary>
    public bool EnableMonitor { get; set; } = true;

    /// <summary>仅手动收藏才入库（Alt+C 主动收录）。</summary>
    public bool ManualOnly { get; set; }

    /// <summary>容量上限，超过后按时间从旧到新清理非置顶/非收藏条目。</summary>
    public int MaxItems { get; set; } = 5000;

    /// <summary>单条内容最大字符数，超出截断（防止一次复制几 MB 日志撑爆索引）。</summary>
    public int MaxContentChars { get; set; } = 200_000;

    /// <summary>主窗口失去焦点时自动隐藏。</summary>
    public bool HideOnLostFocus { get; set; }

    /// <summary>开机自动启动。</summary>
    public bool AutoStart { get; set; } = true;

    public bool FirstRunCompleted { get; set; }

    /// <summary>浅色 / 深色 / 跟随系统。默认浅色。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeMode Theme { get; set; } = ThemeMode.Light;

    /// <summary>界面语言。默认中文。</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public Language Language { get; set; } = Language.Chinese;

    /// <summary>主题色标识（见 UI.Theming.AccentPalettes）。默认青绿。</summary>
    public string AccentColor { get; set; } = "Teal";

    /// <summary>不采集的应用（进程名，不含 .exe，忽略大小写）。</summary>
    public List<string> ExcludedApps { get; set; } = new()
    {
        "1password", "keepass", "keepassxc", "bitwarden", "lastpass", "keepass2",
    };

    public HotKeySettings HotKeys { get; set; } = new();

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.ExcludedApps = new List<string>(ExcludedApps);
        clone.HotKeys = new HotKeySettings
        {
            ToggleWindowEnabled = HotKeys.ToggleWindowEnabled,
            ToggleWindow = HotKeys.ToggleWindow,
            ManualCaptureEnabled = HotKeys.ManualCaptureEnabled,
            ManualCapture = HotKeys.ManualCapture,
        };
        return clone;
    }

    /// <summary>把越界/脏数据拉回合法范围。</summary>
    public void Normalize()
    {
        MaxItems = Math.Clamp(MaxItems, MinItemsLimit, MaxItemsLimit);
        MaxContentChars = Math.Clamp(MaxContentChars, 1_000, 5_000_000);
        ExcludedApps = ExcludedApps
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeAppName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 手工改过 settings.json 或旧版本文件里没有这两个字段时的兜底
        if (!Enum.IsDefined(Theme)) Theme = ThemeMode.Light;
        if (!Enum.IsDefined(Language)) Language = Language.Chinese;
        if (string.IsNullOrWhiteSpace(AccentColor)) AccentColor = "Teal";
    }

    public static string NormalizeAppName(string appName)
    {
        var name = appName.Trim().ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name[..^4] : name;
    }
}
