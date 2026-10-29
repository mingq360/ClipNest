using System.IO;
using ClipNest.Domain;
using ClipNest.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace ClipNest.Tests;

/// <summary>每个测试一个独立临时库，避免互相污染。</summary>
public sealed class TempDatabase : IDisposable
{
    private readonly string _directory;

    public Db Db { get; }

    public SqliteClipRepository Repository { get; }

    public TempDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "clipnest-tests", Guid.NewGuid().ToString("N"));
        Db = new Db(Path.Combine(_directory, "test.db"));
        Db.Initialize();
        Repository = new SqliteClipRepository(Db);
    }

    public long Add(string content, ClipType? type = null, long? atMs = null, bool pinned = false, bool starred = false)
    {
        var now = atMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var clip = new Clip
        {
            Content = content,
            ContentHash = ContentFingerprint.Compute(content),
            Type = type ?? ClipClassifier.Classify(content),
            IsPinned = pinned,
            IsStarred = starred,
            CreatedAtMs = now,
            UpdatedAtMs = now,
        };

        var id = Repository.Upsert(clip);
        if (pinned) Repository.SetPinned(id, true);
        if (starred) Repository.SetStarred(id, true);
        return id;
    }

    public void Dispose()
    {
        // 连接池会一直占着文件句柄，不清空就删不掉目录
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录残留不影响测试结论
        }
    }
}
