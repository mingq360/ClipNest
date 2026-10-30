using ClipNest.Domain;

namespace ClipNest.Tests;

public sealed class RepositoryTests : IDisposable
{
    private readonly TempDatabase _ctx = new();

    public void Dispose() => _ctx.Dispose();

    private IReadOnlyList<Clip> Search(string text) => _ctx.Repository.Query(new ClipQuery { Text = text });

    [Fact]
    public void Upsert_ThenQuery_ReturnsClip()
    {
        _ctx.Add("hello clipnest");

        var items = _ctx.Repository.Query(new ClipQuery());

        Assert.Single(items);
        Assert.Equal("hello clipnest", items[0].Content);
        Assert.Equal(1, items[0].CopyCount);
    }

    [Fact]
    public void Upsert_SameContentTwice_KeepsOneRowAndBumpsCopyCount()
    {
        var first = _ctx.Add("重复的内容");
        var second = _ctx.Add("重复的内容");

        Assert.Equal(first, second);
        Assert.Equal(1, _ctx.Repository.Count());

        var clip = _ctx.Repository.GetById(first)!;
        Assert.Equal(2, clip.CopyCount);
    }

    [Fact]
    public void Upsert_ContentDiffersOnlyByTrailingNewline_IsTreatedAsSame()
    {
        // Windows 与 Unix 换行、尾随换行都不该产生两条记录
        var a = _ctx.Add("SELECT 1\n");
        var b = _ctx.Add("SELECT 1\r\n");

        Assert.Equal(a, b);
        Assert.Equal(1, _ctx.Repository.Count());
    }

    [Fact]
    public void Upsert_InteriorWhitespaceDifference_IsTreatedAsDifferent()
    {
        // 对代码来说 "a = b" 与 "a  =  b" 是不同内容，不能折叠
        _ctx.Add("a = b");
        _ctx.Add("a  =  b");

        Assert.Equal(2, _ctx.Repository.Count());
    }

    [Fact]
    public void Query_ChineseSubstring_MatchesSingleAndDoubleChar()
    {
        _ctx.Add("我的密码是 abc123");
        _ctx.Add("这是一段剪贴板内容");

        Assert.Single(Search("密码"));
        Assert.Single(Search("码"));
        Assert.Single(Search("剪贴板"));
        Assert.Empty(Search("不存在的词"));
    }

    [Fact]
    public void Query_EnglishSubstring_Works()
    {
        _ctx.Add("https://example.com/path?q=1");

        Assert.Single(Search("xample.co"));
        Assert.Single(Search("mp"));
    }

    [Fact]
    public void Query_WildcardCharacters_AreEscaped()
    {
        _ctx.Add("进度 100% 完成");
        _ctx.Add("普通内容没有百分号");

        // 若未转义，"%" 会匹配全部，"_" 会匹配任意单字
        Assert.Single(Search("100%"));
        Assert.Single(Search("%"));
        Assert.Empty(Search("_"));
    }

    [Fact]
    public void Query_AfterDelete_DoesNotReturnStaleIndexRows()
    {
        var id = _ctx.Add("将被删除的剪贴板条目");

        Assert.True(_ctx.Repository.Delete(id));

        Assert.Empty(Search("剪贴板"));
        Assert.Equal(0, _ctx.Repository.Count());
    }

    [Fact]
    public void Upsert_WithExistingHash_KeepsOriginalContent()
    {
        // 片段是「复制时刻的快照」，内容不可变。指纹相同即同一条，不覆盖原文。
        var id = _ctx.Add("原始内容");
        var existing = _ctx.Repository.GetById(id)!;
        existing.Content = "试图改写";

        _ctx.Repository.Upsert(existing);

        Assert.Equal("原始内容", _ctx.Repository.GetById(id)!.Content);
        Assert.Single(Search("原始内容"));
    }

    [Fact]
    public void Query_PinnedItemsComeFirst()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _ctx.Add("较早但置顶", atMs: now - 10_000, pinned: true);
        _ctx.Add("最新的一条", atMs: now);

        var items = _ctx.Repository.Query(new ClipQuery());

        Assert.Equal("较早但置顶", items[0].Content);
    }

    [Fact]
    public void Query_RespectsLimitAndOffset()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < 10; i++) _ctx.Add($"条目 {i}", atMs: now + i);

        var page = _ctx.Repository.Query(new ClipQuery { Limit = 3, Offset = 2 });

        Assert.Equal(3, page.Count);
        Assert.Equal("条目 7", page[0].Content);
    }

    [Fact]
    public void Prune_KeepsMaxItems_AndNeverDeletesPinnedOrStarred()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var pinnedId = _ctx.Add("置顶的老条目", atMs: now - 100_000, pinned: true);
        var starredId = _ctx.Add("收藏的老条目", atMs: now - 90_000, starred: true);
        for (var i = 0; i < 30; i++) _ctx.Add($"普通条目 {i}", atMs: now + i);

        var deleted = _ctx.Repository.Prune(maxItems: 10);

        Assert.True(deleted > 0);
        Assert.True(_ctx.Repository.Count() <= 10, $"删除后仍有 {_ctx.Repository.Count()} 条");
        Assert.NotNull(_ctx.Repository.GetById(pinnedId));
        Assert.NotNull(_ctx.Repository.GetById(starredId));
    }

    [Fact]
    public void Prune_WhenUnderLimit_DoesNothing()
    {
        _ctx.Add("a");
        _ctx.Add("b");

        Assert.Equal(0, _ctx.Repository.Prune(maxItems: 100));
        Assert.Equal(2, _ctx.Repository.Count());
    }

    [Fact]
    public void Clear_KeepProtected_RetainsPinnedAndStarred()
    {
        _ctx.Add("普通", pinned: false);
        _ctx.Add("置顶", pinned: true);
        _ctx.Add("收藏", starred: true);

        var removed = _ctx.Repository.Clear(keepProtected: true);

        Assert.Equal(1, removed);
        Assert.Equal(2, _ctx.Repository.Count());
        // 索引也必须跟着收敛
        Assert.Empty(Search("普通"));
    }

    [Fact]
    public void Clear_All_EmptiesEverything()
    {
        _ctx.Add("甲");
        _ctx.Add("乙");

        _ctx.Repository.Clear(keepProtected: false);

        Assert.Equal(0, _ctx.Repository.Count());
        Assert.Empty(Search("甲"));
    }

    [Fact]
    public void DeleteMany_RemovesOnlyGivenIds()
    {
        var a = _ctx.Add("甲");
        var b = _ctx.Add("乙");
        var c = _ctx.Add("丙");

        var removed = _ctx.Repository.DeleteMany(new[] { a, c });

        Assert.Equal(2, removed);
        Assert.NotNull(_ctx.Repository.GetById(b));
    }

    [Fact]
    public void TypeFilter_ReturnsOnlyMatchingType()
    {
        _ctx.Add("https://example.com");
        _ctx.Add("mail@example.com");
        _ctx.Add("普通一段话");

        var links = _ctx.Repository.Query(new ClipQuery { Type = ClipType.Link });
        var emails = _ctx.Repository.Query(new ClipQuery { Type = ClipType.Email });

        Assert.Single(links);
        Assert.Single(emails);
        Assert.Equal(ClipType.Link, links[0].Type);
    }

    [Fact]
    public void TimeRangeFilter_BoundariesAreInclusive()
    {
        var baseMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _ctx.Add("旧", atMs: baseMs - 5000);
        _ctx.Add("新", atMs: baseMs);

        var items = _ctx.Repository.Query(new ClipQuery { FromMs = baseMs, ToMs = baseMs });

        Assert.Single(items);
        Assert.Equal("新", items[0].Content);
    }

    [Fact]
    public void GetAll_ReturnsChronologicalOrder()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _ctx.Add("第二", atMs: now + 10);
        _ctx.Add("第一", atMs: now);

        var all = _ctx.Repository.GetAll();

        Assert.Equal(new[] { "第一", "第二" }, all.Select(x => x.Content).ToArray());
    }

    [Fact]
    public void UpdateContent_ThenUpsertRefreshedModel_KeepsOneRow()
    {
        // 详情页改过内容之后，下一次「复制该条」会拿着内存模型再 Upsert 一次。
        // 模型必须带上新指纹（对应 ClipRow.UpdateContent 那一步），否则 Upsert 会
        // 以为是一条全新的内容，历史里凭空多一条重复项。
        var id = _ctx.Add("original text");
        _ctx.Repository.UpdateContent(id, "edited text");

        var clip = _ctx.Repository.GetById(id)!;
        clip.UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _ctx.Repository.Upsert(clip);

        Assert.Equal(1, _ctx.Repository.Count());
        Assert.Equal("edited text", _ctx.Repository.GetById(id)!.Content);
    }

    [Fact]
    public void BoolColumns_RoundTrip()
    {
        var id = _ctx.Add("标记测试");
        _ctx.Repository.SetPinned(id, true);
        _ctx.Repository.SetStarred(id, true);
        _ctx.Repository.SetNote(id, "备注内容");

        var clip = _ctx.Repository.GetById(id)!;

        Assert.True(clip.IsPinned);
        Assert.True(clip.IsStarred);
        Assert.Equal("备注内容", clip.Note);
    }
}
