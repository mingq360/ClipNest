using System.IO;
using ClipNest.Domain;
using ClipNest.Infrastructure.Data;
using ClipNest.UI.ViewModels;
using Microsoft.Data.Sqlite;

namespace ClipNest.Tests;

public sealed class PickerViewModelTests : IDisposable
{
    private readonly string _directory;
    private readonly SqliteClipRepository _repository;

    public PickerViewModelTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "clipnest-tests", Guid.NewGuid().ToString("N"));
        var db = new Db(Path.Combine(_directory, "test.db"));
        db.Initialize();
        _repository = new SqliteClipRepository(db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 忽略残留
        }
    }

    private void Add(string content, long atMs)
    {
        _repository.Upsert(new Clip
        {
            Content = content,
            ContentHash = ContentFingerprint.Compute(content),
            Type = ClipClassifier.Classify(content),
            CreatedAtMs = atMs,
            UpdatedAtMs = atMs,
        });
    }

    [Fact]
    public void Load_ListsRecentFirst_AndSelectsTop()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Add("较早的一条", now - 10_000);
        Add("最新的一条", now);

        using var picker = new PickerViewModel(_repository);

        Assert.Equal(2, picker.Items.Count);
        Assert.Equal("最新的一条", picker.Items[0].Content);
        Assert.Equal("最新的一条", picker.SelectedRow?.Content);
        Assert.True(picker.HasSelection);
        Assert.False(picker.IsEmpty);
    }

    [Fact]
    public void Search_FiltersWithinHistory()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Add("我的密码是 abc123", now);
        Add("一段无关的会议纪要", now + 1);

        using var picker = new PickerViewModel(_repository);

        picker.SearchText = "密码";
        picker.Reload(); // 定时器在测试里不会走，直接触发一次

        Assert.Single(picker.Items);
        Assert.Contains("密码", picker.SelectedRow!.Content);
    }

    [Fact]
    public void Search_NoMatch_ShowsEmpty()
    {
        Add("只有这一条", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        using var picker = new PickerViewModel(_repository);

        picker.SearchText = "不存在的关键词";
        picker.Reload();

        Assert.Empty(picker.Items);
        Assert.True(picker.IsEmpty);
        Assert.Null(picker.SelectedRow);
    }

    [Fact]
    public void MoveSelection_ClampsAtBothEnds()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Add("第一条", now + 3);
        Add("第二条", now + 2);
        Add("第三条", now + 1);

        using var picker = new PickerViewModel(_repository);

        picker.MoveSelection(-1); // 已经在顶部，不该翻出去
        Assert.Equal("第一条", picker.SelectedRow?.Content);

        picker.MoveSelection(1);
        Assert.Equal("第二条", picker.SelectedRow?.Content);

        picker.MoveSelection(5);
        Assert.Equal("第三条", picker.SelectedRow?.Content);
    }

    [Fact]
    public void EmptyDatabase_HasNoSelection()
    {
        using var picker = new PickerViewModel(_repository);

        Assert.True(picker.IsEmpty);
        Assert.Null(picker.SelectedRow);

        picker.MoveSelection(1); // 空列表里移动不应抛异常
        Assert.Null(picker.SelectedRow);
    }
}
