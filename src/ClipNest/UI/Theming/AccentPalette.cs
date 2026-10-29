using System.Windows.Media;

namespace ClipNest.UI.Theming;

/// <summary>可选主题色。只放固定色值，深色模式下的提亮由 <see cref="ThemeService"/> 统一算。</summary>
public sealed class AccentPalette
{
    public AccentPalette(string key, string displayName, Color baseColor)
    {
        Key = key;
        DisplayName = displayName;
        BaseColor = baseColor;
        BaseBrush = new SolidColorBrush(baseColor);
        BaseBrush.Freeze();
    }

    public string Key { get; }

    public string DisplayName { get; }

    public Color BaseColor { get; }

    /// <summary>给设置界面的色块用。</summary>
    public SolidColorBrush BaseBrush { get; }
}

public static class AccentPalettes
{
    public const string DefaultKey = "Teal";

    public static IReadOnlyList<AccentPalette> All { get; } = new[]
    {
        new AccentPalette("Teal", "青绿", Color.FromRgb(0x2C, 0x7A, 0x6B)),
        new AccentPalette("Indigo", "靛蓝", Color.FromRgb(0x46, 0x5C, 0xB8)),
        new AccentPalette("Violet", "紫罗兰", Color.FromRgb(0x76, 0x4C, 0xB0)),
        new AccentPalette("Amber", "琥珀", Color.FromRgb(0xB2, 0x74, 0x38)),
        new AccentPalette("Rose", "玫瑰", Color.FromRgb(0xB3, 0x47, 0x66)),
        new AccentPalette("Slate", "石墨", Color.FromRgb(0x4E, 0x5A, 0x60)),
    };

    /// <summary>按 key 取，认不出来就回退默认色（防止用户手改配置写坏）。</summary>
    public static AccentPalette Resolve(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return All[0];

        foreach (var palette in All)
        {
            if (string.Equals(palette.Key, key, StringComparison.OrdinalIgnoreCase)) return palette;
        }

        return All[0];
    }
}
