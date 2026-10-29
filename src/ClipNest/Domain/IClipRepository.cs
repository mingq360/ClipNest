namespace ClipNest.Domain;

/// <summary>
/// 片段仓储。定义在领域层，实现放在基础设施层（依赖倒置）。
/// 全部为同步接口：SQLite 本身无异步 IO，套一层 Task 只是假异步，
/// 调用方需要避免阻塞 UI 时自行用 Task.Run 包裹。
/// </summary>
public interface IClipRepository
{
    /// <summary>
    /// 写入一条片段。内容指纹已存在时不新增行，而是刷新最后复制时间、累加复制次数。
    /// </summary>
    /// <returns>片段的 Id。</returns>
    long Upsert(Clip clip);

    Clip? GetById(long id);

    IReadOnlyList<Clip> Query(ClipQuery query);

    int Count();

    /// <summary>按指纹查找已存在的片段，用于采集前的快速去重判断。</summary>
    Clip? FindByHash(string contentHash);

    bool SetPinned(long id, bool pinned);

    bool SetStarred(long id, bool starred);

    bool SetNote(long id, string? note);

    bool Delete(long id);

    int DeleteMany(IEnumerable<long> ids);

    /// <summary>清空。keepProtected 为 true 时保留置顶与收藏。</summary>
    int Clear(bool keepProtected);

    /// <summary>
    /// 容量清理：总量超过 maxItems 时，按最后复制时间从旧到新删除
    /// **非置顶且非收藏**的片段，一次删到 maxItems - 缓冲量，避免每次写入都触发。
    /// 置顶/收藏永不自动删除，即使它们自身超过 maxItems。
    /// </summary>
    /// <returns>实际删除条数。</returns>
    int Prune(int maxItems);

    /// <summary>导出用：按时间正序全量读取。</summary>
    IReadOnlyList<Clip> GetAll(long? fromMs = null, long? toMs = null);
}
