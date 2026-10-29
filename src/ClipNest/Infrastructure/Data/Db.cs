using System.IO;
using Microsoft.Data.Sqlite;

namespace ClipNest.Infrastructure.Data;

/// <summary>
/// 连接管理 + 结构迁移。
/// 迁移版本记录在 SQLite 文件头的 <c>PRAGMA user_version</c>，
/// 每一版一段 SQL，按序执行，整体包在事务里 —— 失败即回滚，不会留下半旧半新的库。
/// </summary>
public sealed class Db
{
    public const string AppFolderName = "ClipNest";
    public const string DbFileName = "clipnest.db";

    /// <summary>当前代码期望的库结构版本。新增迁移时同步 +1。</summary>
    public const int SchemaVersion = 1;

    public string DatabasePath { get; }

    public Db(string? databasePath = null)
        => DatabasePath = databasePath ?? DefaultDatabasePath();

    public static string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName);

    public static string DefaultDatabasePath() => Path.Combine(DataDirectory, DbFileName);

    /// <summary>打开一个新连接。调用方负责释放（连接池会复用底层句柄）。</summary>
    public SqliteConnection Open()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        };

        var connection = new SqliteConnection(builder.ToString());
        connection.Open();

        // 每条连接都要重设；journal_mode 是持久化属性，只需在 Initialize 里设一次
        Execute(connection, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=3000; PRAGMA synchronous=NORMAL;");
        return connection;
    }

    /// <summary>建库/升级。可重复调用，已是最新版本时立即返回。</summary>
    public void Initialize()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(DatabasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var connection = Open();

        // WAL 不能在事务内切换
        Execute(connection, "PRAGMA journal_mode=WAL;");

        var current = Convert.ToInt32(Scalar(connection, "PRAGMA user_version;"));
        if (current >= SchemaVersion) return;

        for (var version = current + 1; version <= SchemaVersion; version++)
        {
            ApplyMigration(connection, version);
        }
    }

    private static void ApplyMigration(SqliteConnection connection, int version)
    {
        var sql = version switch
        {
            1 => V1,
            _ => throw new InvalidOperationException($"缺少版本 {version} 的迁移脚本"),
        };

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();

        // user_version 不能参数化，version 是代码内常量，无注入面
        command.CommandText = $"PRAGMA user_version = {version};";
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    // ---------------------------------------------------------------- v1
    private const string V1 =
        """
        CREATE TABLE clips (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            content      TEXT    NOT NULL,
            content_hash TEXT    NOT NULL,
            type         INTEGER NOT NULL DEFAULT 0,
            source_app   TEXT,
            source_title TEXT,
            is_pinned    INTEGER NOT NULL DEFAULT 0,
            is_starred   INTEGER NOT NULL DEFAULT 0,
            created_at   INTEGER NOT NULL,
            updated_at   INTEGER NOT NULL,
            copy_count   INTEGER NOT NULL DEFAULT 1,
            note         TEXT
        );

        -- 去重靠指纹唯一约束，而不是「跟上一行比」，A→B→A 不会产生两条 A
        CREATE UNIQUE INDEX ux_clips_hash ON clips(content_hash);
        CREATE INDEX idx_clips_order ON clips(is_pinned DESC, updated_at DESC);
        CREATE INDEX idx_clips_type ON clips(type);

        CREATE TABLE tags (
            id   INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL UNIQUE
        );

        CREATE TABLE clip_tags (
            clip_id INTEGER NOT NULL REFERENCES clips(id) ON DELETE CASCADE,
            tag_id  INTEGER NOT NULL REFERENCES tags(id)  ON DELETE CASCADE,
            PRIMARY KEY (clip_id, tag_id)
        );

        -- 全文索引：trigram 分词，中文 1 字起就能命中子串（MATCH 需 >=3 字，故统一走 LIKE）
        -- external content 模式：正文只存一份在 clips 表，索引本身不复制原文
        CREATE VIRTUAL TABLE clips_fts USING fts5(
            content,
            content='clips',
            content_rowid='id',
            tokenize='trigram'
        );

        CREATE TRIGGER clips_ai AFTER INSERT ON clips BEGIN
            INSERT INTO clips_fts(rowid, content) VALUES (new.id, new.content);
        END;

        CREATE TRIGGER clips_ad AFTER DELETE ON clips BEGIN
            INSERT INTO clips_fts(clips_fts, rowid, content) VALUES ('delete', old.id, old.content);
        END;

        -- 只有正文变化才重建索引行；刷新 updated_at / copy_count 不触发
        CREATE TRIGGER clips_au AFTER UPDATE OF content ON clips BEGIN
            INSERT INTO clips_fts(clips_fts, rowid, content) VALUES ('delete', old.id, old.content);
            INSERT INTO clips_fts(rowid, content) VALUES (new.id, new.content);
        END;
        """;
}
