namespace ClipNest.Domain;

/// <summary>
/// 检索条件。默认按「置顶优先 → 最后复制时间倒序」排序。
/// </summary>
public sealed class ClipQuery
{
    /// <summary>子串关键词。为空表示不做文本过滤。</summary>
    public string? Text { get; init; }

    public ClipType? Type { get; init; }

    public bool OnlyStarred { get; init; }

    /// <summary>仅显示置顶片段（托盘菜单/筛选用）。</summary>
    public bool OnlyPinned { get; init; }

    public long? FromMs { get; init; }

    public long? ToMs { get; init; }

    public int Limit { get; init; } = 500;

    public int Offset { get; init; }

    public bool HasTextFilter => !string.IsNullOrWhiteSpace(Text);
}
