using System.Security.Cryptography;
using System.Text;

namespace ClipNest.Domain;

/// <summary>
/// 内容指纹：用于去重，不是原文的哈希。
/// 归一化只做「不改语义」的两件事：
///   1. 换行统一为 \n（Windows/Unix 混用不应产生两条记录）
///   2. 去掉末尾空白（很多编辑器复制时会带一个尾随换行）
/// 刻意**不**折叠内部空白 —— 对代码片段来说 "a  =  b" 与 "a = b" 是不同内容。
/// </summary>
public static class ContentFingerprint
{
    /// <summary>原文是否值得收录。纯空白不收录。</summary>
    public static bool IsWorthCapturing(string? content)
        => !string.IsNullOrWhiteSpace(content);

    public static string Compute(string content)
    {
        var normalized = Normalize(content);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Normalize(string content)
        => content.Replace("\r\n", "\n").Replace('\r', '\n').TrimEnd();

    /// <summary>按最大字符数截断，避免超大文本撑爆索引。返回是否发生了截断。</summary>
    public static bool TryTruncate(string content, int maxChars, out string result)
    {
        if (maxChars <= 0 || content.Length <= maxChars)
        {
            result = content;
            return false;
        }

        // 不在代理对中间切断，否则会产生非法 UTF-16
        var cut = maxChars;
        if (cut > 0 && char.IsHighSurrogate(content[cut - 1])) cut--;

        result = string.Concat(content.AsSpan(0, cut), "\n…（内容过长，已截断）");
        return true;
    }
}
