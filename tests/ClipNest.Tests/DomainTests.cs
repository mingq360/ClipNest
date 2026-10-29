using ClipNest.Domain;

namespace ClipNest.Tests;

public sealed class ClipClassifierTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://localhost:8080/api?x=1")]
    [InlineData("www.example.com/path")]
    [InlineData("  https://example.com/a  ")]
    public void HttpUrl_IsLink(string content)
        => Assert.Equal(ClipType.Link, ClipClassifier.Classify(content));

    [Theory]
    [InlineData("user@example.com")]
    [InlineData("first.last+tag@mail.example.co.uk")]
    public void EmailLike_IsEmail(string content)
        => Assert.Equal(ClipType.Email, ClipClassifier.Classify(content));

    [Theory]
    [InlineData("13812345678")]
    [InlineData("1,234.56")]
    [InlineData("+86 138 1234 5678")]
    public void NumericOnly_IsNumber(string content)
        => Assert.Equal(ClipType.Number, ClipClassifier.Classify(content));

    [Theory]
    [InlineData("普通的一段文字")]
    [InlineData("2026-10-29")]                       // 带连字符的日期不当数字
    [InlineData("abc@")]                              // 不完整邮箱
    [InlineData("https://example.com\n第二行")]        // 多行不当链接
    [InlineData("SELECT * FROM users")]               // MVP 不识别代码
    [InlineData("")]
    [InlineData("   ")]
    public void Others_AreText(string content)
        => Assert.Equal(ClipType.Text, ClipClassifier.Classify(content));

    [Fact]
    public void UrlWithSpace_IsNotLink()
        => Assert.NotEqual(ClipType.Link, ClipClassifier.Classify("https://example.com/a b"));
}

public sealed class ContentFingerprintTests
{
    [Fact]
    public void LineEndings_AreNormalized()
        => Assert.Equal(ContentFingerprint.Compute("a\r\nb"), ContentFingerprint.Compute("a\nb"));

    [Fact]
    public void TrailingWhitespace_IsIgnored()
        => Assert.Equal(ContentFingerprint.Compute("abc"), ContentFingerprint.Compute("abc\n\n "));

    [Fact]
    public void LeadingWhitespace_Matters()
        => Assert.NotEqual(ContentFingerprint.Compute("abc"), ContentFingerprint.Compute("  abc"));

    [Fact]
    public void InteriorWhitespace_Matters()
        => Assert.NotEqual(ContentFingerprint.Compute("a b"), ContentFingerprint.Compute("a  b"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n\t ")]
    public void BlankContent_IsNotWorthCapturing(string? content)
        => Assert.False(ContentFingerprint.IsWorthCapturing(content));

    [Fact]
    public void Truncate_CutsAtLimitAndMarksIt()
    {
        var truncated = ContentFingerprint.TryTruncate(new string('x', 100), 10, out var result);

        Assert.True(truncated);
        Assert.StartsWith(new string('x', 10), result);
        Assert.Contains("已截断", result);
    }

    [Fact]
    public void Truncate_DoesNotSplitSurrogatePair()
    {
        // 😀 是代理对，从中间切断会产生非法 UTF-16
        var content = string.Concat(Enumerable.Repeat("😀", 10));
        ContentFingerprint.TryTruncate(content, 5, out var result);

        var body = result[..result.IndexOf('\n')];

        // 5 会切在第 3 个 emoji 的中间，必须退到 4 个码元（= 2 个完整 emoji）
        Assert.Equal(4, body.Length);
        Assert.Equal(string.Concat(Enumerable.Repeat("😀", 2)), body);
    }

    [Fact]
    public void Truncate_ShortContent_IsUntouched()
    {
        var truncated = ContentFingerprint.TryTruncate("short", 100, out var result);

        Assert.False(truncated);
        Assert.Equal("short", result);
    }
}
