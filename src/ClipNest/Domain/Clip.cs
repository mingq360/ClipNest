using System.Text.RegularExpressions;

namespace ClipNest.Domain;

/// <summary>
/// 剪贴板片段实体。
/// 时间统一以 Unix 毫秒存储（对应 SQLite INTEGER），对外暴露 DateTimeOffset。
/// </summary>
public sealed partial class Clip
{
    public long Id { get; set; }

    /// <summary>原文，不做任何裁剪。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>去重用的内容指纹（由 <see cref="ContentFingerprint"/> 生成，非原文哈希）。</summary>
    public string ContentHash { get; set; } = string.Empty;

    public ClipType Type { get; set; }

    /// <summary>来源进程名，如 chrome。可能为空（无法解析时）。</summary>
    public string? SourceApp { get; set; }

    /// <summary>来源窗口标题，可能为空。</summary>
    public string? SourceTitle { get; set; }

    public bool IsPinned { get; set; }

    public bool IsStarred { get; set; }

    /// <summary>首次收录时间。</summary>
    public long CreatedAtMs { get; set; }

    /// <summary>最后一次被复制的时间（去重命中时刷新）。</summary>
    public long UpdatedAtMs { get; set; }

    /// <summary>被复制过几次（去重命中时累加）。</summary>
    public int CopyCount { get; set; } = 1;

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt => DateTimeOffset.FromUnixTimeMilliseconds(CreatedAtMs).ToLocalTime();

    public DateTimeOffset UpdatedAt => DateTimeOffset.FromUnixTimeMilliseconds(UpdatedAtMs).ToLocalTime();

    public int Length => Content.Length;

    /// <summary>单行预览：合并空白、截断。用于列表行，避免把多行内容塞进一行渲染。</summary>
    public string Preview(int maxLength = 160)
    {
        var text = WhitespaceRun().Replace(Content, " ").Trim();
        if (text.Length <= maxLength) return text;
        return string.Concat(text.AsSpan(0, maxLength), "…");
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();
}
