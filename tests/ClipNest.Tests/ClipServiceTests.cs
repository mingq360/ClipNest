using System.IO;
using ClipNest.Domain;
using ClipNest.Infrastructure.Data;
using ClipNest.Services;
using Microsoft.Data.Sqlite;

namespace ClipNest.Tests;

public sealed class ClipServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly ClipService _service;
    private readonly SettingsService _settings;

    public ClipServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "clipnest-tests", Guid.NewGuid().ToString("N"));
        var db = new Db(Path.Combine(_directory, "test.db"));
        db.Initialize();

        _settings = new SettingsService(Path.Combine(_directory, "settings.json"));
        _settings.Load();

        _service = new ClipService(new SqliteClipRepository(db), _settings);
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

    [Fact]
    public void Capture_WhenPaused_IsIgnored()
    {
        _service.SetPaused(true);

        Assert.Null(_service.Capture("暂停期间复制的内容", "chrome", "标题"));
    }

    [Fact]
    public void Capture_ManualBypassesPause()
    {
        _service.SetPaused(true);

        // 用户主动按快捷键收藏，即使在暂停中也要生效
        Assert.NotNull(_service.Capture("主动收藏的内容", "chrome", "标题", manual: true));
    }

    [Fact]
    public void Capture_ManualOnlyMode_IgnoresPassiveCopy()
    {
        _settings.Update(s => s.ManualOnly = true);

        Assert.Null(_service.Capture("普通复制", "chrome", "标题"));
        Assert.NotNull(_service.Capture("主动收藏", "chrome", "标题", manual: true));
    }

    [Theory]
    [InlineData("1password")]
    [InlineData("1password.exe")]
    [InlineData("1Password.EXE")]
    public void Capture_ExcludedApp_IsIgnored(string appName)
    {
        // 默认排除名单里带 1password
        Assert.Null(_service.Capture("密码内容", appName, "标题"));
    }

    [Fact]
    public void Capture_NonExcludedApp_IsSaved()
    {
        Assert.NotNull(_service.Capture("正常内容", "chrome", "标题"));
    }

    [Fact]
    public void Capture_ManualBypassesWhitelist()
    {
        // 用户明确要求收藏，就不该再被白名单拦
        Assert.NotNull(_service.Capture("手动收藏", "1password", "标题", manual: true));
    }

    [Fact]
    public void Capture_BlankContent_IsIgnored()
    {
        Assert.Null(_service.Capture("   \r\n\t ", "chrome", "标题"));
        Assert.Null(_service.Capture(string.Empty, "chrome", "标题"));
    }

    [Fact]
    public void Capture_TruncatesOverlongContent()
    {
        // 上限会被 Normalize 夹到 >= 1000，所以这里用 1000 作界限
        _settings.Update(s => s.MaxContentChars = 1000);

        var saved = _service.Capture(new string('字', 3000), "chrome", "标题");

        Assert.NotNull(saved);
        Assert.True(saved!.Content.Length < 3000);
        Assert.Contains("已截断", saved.Content);
    }

    [Fact]
    public void Capture_SameContentTwice_BumpsCopyCountInsteadOfDuplicating()
    {
        var first = _service.Capture("同一段内容", "chrome", "标题");
        var second = _service.Capture("同一段内容", "vscode", "另一个标题");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(2, second.CopyCount);
        Assert.Equal(1, _service.Repository.Count());
    }

    [Fact]
    public void Capture_NormalizesSourceAppName()
    {
        var saved = _service.Capture("内容", "Chrome.EXE", "标题");

        Assert.Equal("chrome", saved!.SourceApp);
    }

    [Fact]
    public void Capture_ClassifiesType()
    {
        Assert.Equal(ClipType.Link, _service.Capture("https://example.com", "chrome", "")!.Type);
        Assert.Equal(ClipType.Email, _service.Capture("a@b.com", "chrome", "")!.Type);
    }

    [Fact]
    public void Capture_TriggersCapacityPrune()
    {
        _settings.Update(s => s.MaxItems = AppSettings.MinItemsLimit);

        // 上限 100，写 130 条应触发一次清理
        for (var i = 0; i < 130; i++)
        {
            _service.Capture($"条目编号 {i}", "chrome", "标题");
        }

        Assert.True(_service.Repository.Count() <= AppSettings.MinItemsLimit,
            $"清理后仍有 {_service.Repository.Count()} 条，超过上限");
    }
}
