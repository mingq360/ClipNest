using System.Text.RegularExpressions;

namespace ClipNest.Domain;

/// <summary>
/// 片段类型判定。MVP 只做四条**确定**规则：链接 / 邮箱 / 数字 / 文本。
/// 刻意不做「代码」自动识别 —— 命令行、JSON、日志都能被误判成代码，
/// 误判比不判更伤可信度，故 <see cref="ClipType.Code"/> 留给 P1 或用户手动标注。
/// </summary>
public static class ClipClassifier
{
    private static readonly Regex EmailPattern = new(
        @"^[^@\s]{1,64}@[^@\s.]+(\.[^@\s.]+)+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NumberPattern = new(
        @"^[+-]?[\d][\d,.\s]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static ClipType Classify(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return ClipType.Text;

        var text = content.Trim();

        if (IsLink(text)) return ClipType.Link;

        // 多行内容一律视为文本：邮箱/数字不可能是多行，多行 URL 也不该当链接
        if (text.Contains('\n')) return ClipType.Text;

        if (IsEmail(text)) return ClipType.Email;
        if (IsNumber(text)) return ClipType.Number;

        return ClipType.Text;
    }

    private static bool IsLink(string text)
    {
        if (text.Length > 2048) return false;

        // URL 里不允许出现空白
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) return false;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp
                || uri.Scheme == Uri.UriSchemeHttps
                || uri.Scheme == Uri.UriSchemeFtp
                || uri.Scheme == Uri.UriSchemeFile))
        {
            return true;
        }

        // 省略 scheme 的常见写法：www.example.com/path
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && text.Contains('.'))
        {
            return true;
        }

        return false;
    }

    private static bool IsEmail(string text)
        => text.Length <= 254 && EmailPattern.IsMatch(text);

    private static bool IsNumber(string text)
    {
        if (text.Length is 0 or > 32) return false;
        if (!NumberPattern.IsMatch(text)) return false;

        // 必须含数字，且不能只有符号/分隔符
        foreach (var ch in text)
        {
            if (char.IsAsciiDigit(ch)) return true;
        }

        return false;
    }
}
