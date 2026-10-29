namespace ClipNest.Domain;

/// <summary>
/// 片段类型。数值写入数据库，**不得随意改动顺序**。
/// </summary>
public enum ClipType
{
    Text = 0,
    Link = 1,
    Code = 2,
    Email = 3,
    Number = 4,
}

public static class ClipTypeExtensions
{
    public static string ToDisplayName(this ClipType type) => type switch
    {
        ClipType.Link => "链接",
        ClipType.Code => "代码",
        ClipType.Email => "邮箱",
        ClipType.Number => "数字",
        _ => "文本",
    };

    /// <summary>单字符标记，用于列表左侧的类型角标。</summary>
    public static string ToGlyph(this ClipType type) => type switch
    {
        ClipType.Link => "链",
        ClipType.Code => "码",
        ClipType.Email => "@",
        ClipType.Number => "数",
        _ => "文",
    };
}
