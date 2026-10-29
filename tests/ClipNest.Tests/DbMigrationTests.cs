using ClipNest.Infrastructure.Data;
using Microsoft.Data.Sqlite;

namespace ClipNest.Tests;

public sealed class DbMigrationTests : IDisposable
{
    private readonly TempDatabase _ctx = new();

    public void Dispose() => _ctx.Dispose();

    [Fact]
    public void Initialize_SetsSchemaVersion()
    {
        using var connection = _ctx.Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";

        Assert.Equal((long)Db.SchemaVersion, command.ExecuteScalar());
    }

    [Fact]
    public void Initialize_IsIdempotent()
    {
        // 重复初始化不能报错、不能重复建表
        _ctx.Db.Initialize();
        _ctx.Db.Initialize();

        using var connection = _ctx.Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='clips';";

        Assert.Equal(1L, command.ExecuteScalar());
    }

    [Fact]
    public void Initialize_EnablesWalMode()
    {
        using var connection = _ctx.Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";

        Assert.Equal("wal", Convert.ToString(command.ExecuteScalar())!.ToLowerInvariant());
    }

    [Fact]
    public void FtsIndex_IsConsistentWithContentTable()
    {
        _ctx.Add("完整性校验用的一条剪贴板内容");

        using var connection = _ctx.Db.Open();
        using var command = connection.CreateCommand();
        // FTS5 自带的一致性检查，不一致会抛异常
        command.CommandText = "INSERT INTO clips_fts(clips_fts) VALUES('integrity-check');";

        command.ExecuteNonQuery();
    }

    [Fact]
    public void ForeignKeys_AreEnforced()
    {
        using var connection = _ctx.Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";

        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }
}
