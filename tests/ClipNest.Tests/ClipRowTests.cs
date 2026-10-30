using ClipNest.Domain;
using ClipNest.Services;
using ClipNest.UI.ViewModels;

namespace ClipNest.Tests;

/// <summary>
/// 列表行视图模型。<see cref="ClipRow"/> 承担「片段 → 界面文本」的翻译，
/// 这里守住几件容易改回去的事。
/// </summary>
public sealed class ClipRowTests
{
    private static ClipRow Create(string content) => new(new Clip
    {
        Content = content,
        ContentHash = ContentFingerprint.Compute(content),
        Type = ClipClassifier.Classify(content),
        CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    });

    [Fact]
    public void UpdateContent_RefreshesFingerprint()
    {
        // 回归：早先只在 UI 侧改了 Content，指纹仍是旧值。
        // 之后「复制该条」会用内存模型里的指纹走 Upsert —— 旧指纹在库里撞不到任何行，
        // 于是插入一条内容相同的重复记录，去重形同失效。
        var row = Create("alpha");

        row.UpdateContent("beta");

        Assert.Equal("beta", row.Content);
        Assert.Equal(ContentFingerprint.Compute("beta"), row.Model.ContentHash);
    }

    [Fact]
    public void UpdateContent_ReclassifiesType()
    {
        var row = Create("plain prose");

        row.UpdateContent("https://example.com/post");

        Assert.Equal(ClipType.Link, row.Model.Type);
    }

    [Fact]
    public void SourceText_WithoutSourceApp_UsesLocalizedFallback()
    {
        var row = Create("x");

        // 来源为空时不应把某个语言的字串写死在代码里
        Assert.Equal(SourceFallback(), row.SourceText);
    }

    /// <summary>测试进程里没有 Application，Loc 会退回 key 本身，这里比对同样的取值结果。</summary>
    private static string SourceFallback() => ClipNest.UI.Localization.Loc.Get("Str_UnknownSource");

    [Fact]
    public void TypeGlyph_ResolvesToResourceKey()
    {
        // 角标/类型名统一按语言取（Loc 缺席时退回 key），不再由领域层写死中文
        Assert.Equal("Str_GlyphLink", Create("https://example.com").TypeGlyph);
        Assert.Equal("Str_TypeLink", Create("https://example.com").TypeName);
    }
}

/// <summary>
/// 快捷键解析只回错误码、不带界面文案 —— 文案由 UI 层按语言取，
/// 否则中文错误提示会漏进英文界面。
/// </summary>
public sealed class HotKeyGestureTests
{
    [Fact]
    public void TryParse_Empty_FailsWithEmptyKind()
    {
        var result = HotKeyGesture.TryParse("  ", out _, out _);

        Assert.False(result.Ok);
        Assert.Equal(HotKeyErrorKind.Empty, result.Kind);
    }

    [Fact]
    public void TryParse_NoModifier_FailsWithMissingModifier()
    {
        var result = HotKeyGesture.TryParse("V", out _, out _);

        Assert.False(result.Ok);
        Assert.Equal(HotKeyErrorKind.MissingModifier, result.Kind);
    }

    [Fact]
    public void TryParse_UnknownModifier_FailsAndReportsTheToken()
    {
        var result = HotKeyGesture.TryParse("Alt+Ctlr+V", out _, out _);

        Assert.False(result.Ok);
        Assert.Equal(HotKeyErrorKind.UnknownModifier, result.Kind);
        Assert.Equal("Ctlr", result.Detail);
    }

    [Fact]
    public void TryParse_UnknownKey_FailsAndReportsTheToken()
    {
        var result = HotKeyGesture.TryParse("Alt+Nope", out _, out _);

        Assert.False(result.Ok);
        Assert.Equal(HotKeyErrorKind.UnknownKey, result.Kind);
        Assert.Equal("Nope", result.Detail);
    }

    [Fact]
    public void TryParse_ValidGesture_Succeeds()
    {
        var result = HotKeyGesture.TryParse("Ctrl+Alt+V", out var modifiers, out var virtualKey);

        Assert.True(result.Ok);
        Assert.Equal(HotKeyErrorKind.None, result.Kind);
        Assert.NotEqual(0, modifiers);
        Assert.NotEqual(0, virtualKey);
    }
}
