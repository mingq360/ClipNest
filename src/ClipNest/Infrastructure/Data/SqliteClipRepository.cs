using System.Text;
using ClipNest.Domain;
using Dapper;
using Microsoft.Data.Sqlite;

namespace ClipNest.Infrastructure.Data;

/// <summary>
/// SQLite 实现。每次操作开一个短连接（连接池复用），避免采集线程与 UI 线程共享句柄带来的锁竞争。
/// </summary>
public sealed class SqliteClipRepository : IClipRepository
{
    /// <summary>手写列别名：Dapper 按属性名匹配，snake_case 列名必须显式对齐。</summary>
    private const string ClipColumns = """
        c.id           AS Id,
        c.content      AS Content,
        c.content_hash AS ContentHash,
        c.type         AS Type,
        c.source_app   AS SourceApp,
        c.source_title AS SourceTitle,
        c.is_pinned    AS IsPinned,
        c.is_starred   AS IsStarred,
        c.created_at   AS CreatedAtMs,
        c.updated_at   AS UpdatedAtMs,
        c.copy_count   AS CopyCount,
        c.note         AS Note
        """;

    private const string UpsertSql = """
        INSERT INTO clips (content, content_hash, type, source_app, source_title, created_at, updated_at, copy_count)
        VALUES (@Content, @ContentHash, @Type, @SourceApp, @SourceTitle, @NowMs, @NowMs, 1)
        ON CONFLICT(content_hash) DO UPDATE SET
            updated_at   = excluded.updated_at,
            copy_count   = copy_count + 1,
            source_app   = COALESCE(excluded.source_app, source_app),
            source_title = COALESCE(excluded.source_title, source_title)
        RETURNING id;
        """;

    private readonly Db _db;

    static SqliteClipRepository()
    {
        // SQLite 的 INTEGER 读出来是 long，Dapper 默认的枚举转换会按 int 拆箱而失败，这里显式处理
        SqlMapper.AddTypeHandler(new ClipTypeHandler());
    }

    public SqliteClipRepository(Db db) => _db = db;

    public long Upsert(Clip clip)
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<long>(UpsertSql, new
        {
            clip.Content,
            clip.ContentHash,
            Type = (int)clip.Type,
            clip.SourceApp,
            clip.SourceTitle,
            NowMs = clip.UpdatedAtMs,
        });
    }

    public Clip? GetById(long id)
    {
        using var connection = _db.Open();
        return connection.QuerySingleOrDefault<Clip>(
            $"SELECT {ClipColumns} FROM clips c WHERE c.id = @id", new { id });
    }

    public Clip? FindByHash(string contentHash)
    {
        using var connection = _db.Open();
        return connection.QuerySingleOrDefault<Clip>(
            $"SELECT {ClipColumns} FROM clips c WHERE c.content_hash = @contentHash", new { contentHash });
    }

    public IReadOnlyList<Clip> Query(ClipQuery query)
    {
        var sql = new StringBuilder("SELECT ").Append(ClipColumns).Append(" FROM clips c");
        var conditions = new List<string>(5);
        var args = new DynamicParameters();

        if (query.HasTextFilter)
        {
            // 走 trigram 索引：LIKE 在 fts5 trigram 表上可被索引加速，且 1 字查询也正确（MATCH 需 >=3 字）
            sql.Append(" JOIN clips_fts f ON f.rowid = c.id");
            conditions.Add(@"f.content LIKE @pattern ESCAPE '\'");
            args.Add("pattern", BuildLikePattern(query.Text!));
        }

        if (query.Type is { } type)
        {
            conditions.Add("c.type = @type");
            args.Add("type", (int)type);
        }

        if (query.OnlyStarred) conditions.Add("c.is_starred = 1");
        if (query.OnlyPinned) conditions.Add("c.is_pinned = 1");

        if (query.FromMs is { } fromMs)
        {
            conditions.Add("c.updated_at >= @fromMs");
            args.Add("fromMs", fromMs);
        }

        if (query.ToMs is { } toMs)
        {
            conditions.Add("c.updated_at <= @toMs");
            args.Add("toMs", toMs);
        }

        if (conditions.Count > 0) sql.Append(" WHERE ").Append(string.Join(" AND ", conditions));

        sql.Append(" ORDER BY c.is_pinned DESC, c.updated_at DESC, c.id DESC LIMIT @limit OFFSET @offset");

        args.Add("limit", query.Limit <= 0 ? 500 : query.Limit);
        args.Add("offset", Math.Max(0, query.Offset));

        using var connection = _db.Open();
        return connection.Query<Clip>(sql.ToString(), args).AsList();
    }

    public int Count()
    {
        using var connection = _db.Open();
        return connection.ExecuteScalar<int>("SELECT COUNT(*) FROM clips;");
    }

    public bool SetPinned(long id, bool pinned)
        => Execute($"UPDATE clips SET is_pinned = {(pinned ? 1 : 0)} WHERE id = @id", new { id }) > 0;

    public bool SetStarred(long id, bool starred)
        => Execute($"UPDATE clips SET is_starred = {(starred ? 1 : 0)} WHERE id = @id", new { id }) > 0;

    public bool SetNote(long id, string? note)
        => Execute("UPDATE clips SET note = @note WHERE id = @id", new { id, note }) > 0;

    public bool UpdateContent(long id, string content)
    {
        // content 改变后指纹随之改变：去重按新指纹走，未来相同内容会命中也更合理。
        // clips_au 触发器会自动同步 clips_fts 全文索引，无需手动维护。
        var hash = ContentFingerprint.Compute(content);
        return Execute(
            "UPDATE clips SET content = @content, content_hash = @hash, updated_at = @now WHERE id = @id",
            new { id, content, hash, now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() }) > 0;
    }

    public bool Delete(long id)
        => Execute("DELETE FROM clips WHERE id = @id", new { id }) > 0;

    public int DeleteMany(IEnumerable<long> ids)
    {
        var list = ids as ICollection<long> ?? ids.ToList();
        if (list.Count == 0) return 0;

        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();
        var affected = connection.Execute(
            "DELETE FROM clips WHERE id IN @ids", new { ids = list }, transaction);
        transaction.Commit();
        return affected;
    }

    public int Clear(bool keepProtected)
    {
        var sql = keepProtected
            ? "DELETE FROM clips WHERE is_pinned = 0 AND is_starred = 0;"
            : "DELETE FROM clips;";

        using var connection = _db.Open();
        using var transaction = connection.BeginTransaction();
        var affected = connection.Execute(sql, transaction: transaction);

        if (!keepProtected)
        {
            // 清空后显式重建索引，避免历史脏段残留在 FTS 表里
            connection.Execute("INSERT INTO clips_fts(clips_fts) VALUES('rebuild');", transaction: transaction);
        }

        transaction.Commit();
        return affected;
    }

    public int Prune(int maxItems)
    {
        if (maxItems <= 0) return 0;

        using var connection = _db.Open();
        var total = connection.ExecuteScalar<int>("SELECT COUNT(*) FROM clips;");
        if (total <= maxItems) return 0;

        // 留缓冲避免每写一条就删一次；缓冲不得超过上限的一半，否则小上限会把库删空
        var buffer = Math.Min(maxItems / 2, Math.Max(20, maxItems / 20));
        var target = Math.Max(0, maxItems - buffer);
        var excess = total - target;

        using var transaction = connection.BeginTransaction();
        var affected = connection.Execute(
            """
            DELETE FROM clips WHERE id IN (
                SELECT id FROM clips
                WHERE is_pinned = 0 AND is_starred = 0
                ORDER BY updated_at ASC, id ASC
                LIMIT @excess
            );
            """,
            new { excess }, transaction);
        transaction.Commit();
        return affected;
    }

    public IReadOnlyList<Clip> GetAll(long? fromMs = null, long? toMs = null)
    {
        var sql = new StringBuilder($"SELECT {ClipColumns} FROM clips c");
        var clauses = new List<string>(2);

        if (fromMs is not null) clauses.Add("c.updated_at >= @fromMs");
        if (toMs is not null) clauses.Add("c.updated_at <= @toMs");
        if (clauses.Count > 0) sql.Append(" WHERE ").Append(string.Join(" AND ", clauses));

        sql.Append(" ORDER BY c.updated_at ASC, c.id ASC");

        using var connection = _db.Open();
        return connection.Query<Clip>(sql.ToString(), new { fromMs, toMs }).AsList();
    }

    private int Execute(string sql, object? args = null)
    {
        using var connection = _db.Open();
        return connection.Execute(sql, args);
    }

    /// <summary>转义 LIKE 通配符，否则用户搜 <c>%</c> 会命中全部内容。</summary>
    private static string BuildLikePattern(string text)
        => "%" + text.Trim()
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_") + "%";

    private sealed class ClipTypeHandler : SqlMapper.TypeHandler<ClipType>
    {
        public override void SetValue(System.Data.IDbDataParameter parameter, ClipType value)
            => parameter.Value = (int)value;

        public override ClipType Parse(object value) => (ClipType)Convert.ToInt32(value);
    }
}
