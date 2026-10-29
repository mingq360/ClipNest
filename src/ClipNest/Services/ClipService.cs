using ClipNest.Domain;
using ClipNest.Infrastructure.Diagnostics;

namespace ClipNest.Services;

/// <summary>
/// 采集编排：暂停/白名单/去重/截断/容量策略都在这里，UI 与监听器都不碰这些规则。
/// </summary>
public sealed class ClipService
{
    private readonly IClipRepository _repository;
    private readonly SettingsService _settings;
    private bool _paused;

    public ClipService(IClipRepository repository, SettingsService settings)
    {
        _repository = repository;
        _settings = settings;
    }

    /// <summary>有新片段入库（含去重命中后的刷新）。</summary>
    public event EventHandler<Clip>? ClipSaved;

    public event EventHandler<bool>? PausedChanged;

    /// <summary>容量清理删掉了旧条目。</summary>
    public event EventHandler<int>? Pruned;

    public bool IsPaused => _paused;

    public IClipRepository Repository => _repository;

    public AppSettings Settings => _settings.Current;

    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;

        _paused = paused;
        Log.Info(paused ? "已暂停采集" : "已恢复采集");
        PausedChanged?.Invoke(this, paused);
    }

    public void TogglePause() => SetPaused(!_paused);

    /// <summary>来源应用是否在排除名单内。</summary>
    public bool IsExcluded(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return false;

        var normalized = AppSettings.NormalizeAppName(appName);
        foreach (var excluded in _settings.Current.ExcludedApps)
        {
            if (string.Equals(excluded, normalized, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>
    /// 处理一段剪贴板文本。
    /// </summary>
    /// <param name="manual">true 表示用户主动收藏，绕过「仅手动模式」与暂停判断。</param>
    /// <returns>入库的片段；被各种规则忽略时返回 null。</returns>
    public Clip? Capture(string text, string? sourceApp, string? sourceTitle, bool manual = false)
    {
        if (!manual && _paused) return null;
        if (!ContentFingerprint.IsWorthCapturing(text)) return null;
        if (!manual && _settings.Current.ManualOnly) return null;
        if (!manual && IsExcluded(sourceApp)) return null;

        var content = text;
        var maxChars = _settings.Current.MaxContentChars;
        if (ContentFingerprint.TryTruncate(text, maxChars, out var truncated))
        {
            content = truncated;
            Log.Info($"单条内容超过 {maxChars} 字符，已截断（原长 {text.Length}）");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var clip = new Clip
        {
            Content = content,
            ContentHash = ContentFingerprint.Compute(content),
            Type = ClipClassifier.Classify(content),
            SourceApp = NormalizeSourceApp(sourceApp),
            SourceTitle = string.IsNullOrWhiteSpace(sourceTitle) ? null : Truncate(sourceTitle, 200),
            CreatedAtMs = now,
            UpdatedAtMs = now,
        };

        clip.Id = _repository.Upsert(clip);

        var pruned = _repository.Prune(_settings.Current.MaxItems);
        if (pruned > 0)
        {
            Log.Info($"容量超限，已清理 {pruned} 条最旧片段");
            Pruned?.Invoke(this, pruned);
        }

        // 回读一次拿到权威的复制次数与时间（去重命中时这两项由数据库更新）
        var saved = _repository.GetById(clip.Id) ?? clip;
        ClipSaved?.Invoke(this, saved);
        return saved;
    }

    private static string? NormalizeSourceApp(string? appName)
    {
        if (string.IsNullOrWhiteSpace(appName)) return null;

        var normalized = AppSettings.NormalizeAppName(appName);
        return normalized.Length == 0 ? null : Truncate(normalized, 100);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}
