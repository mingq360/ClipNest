using ClipNest.Domain;
using ClipNest.UI.Infrastructure;

namespace ClipNest.UI.ViewModels;

/// <summary>
/// 列表行的视图模型。包一层是为了让「相对时间」这类显示文本能就地刷新，
/// 而不必为了时间变化重查数据库。
/// </summary>
public sealed class ClipRow : ObservableObject
{
    public ClipRow(Clip model) => Model = model;

    public Clip Model { get; }

    public long Id => Model.Id;

    public string Content => Model.Content;

    public ClipType Type => Model.Type;

    public string TypeGlyph => Model.Type.ToGlyph();

    public string TypeName => Model.Type.ToDisplayName();

    public bool IsPinned => Model.IsPinned;

    public bool IsStarred => Model.IsStarred;

    public bool HasNote => !string.IsNullOrWhiteSpace(Model.Note);

    public string Preview => Model.Preview();

    public string SourceText => string.IsNullOrWhiteSpace(Model.SourceApp) ? "未知来源" : Model.SourceApp!;

    public string MetaText
    {
        get
        {
            var parts = new List<string>(3) { SourceText };

            if (!string.IsNullOrWhiteSpace(Model.Note)) parts.Add(Model.Note!);
            if (Model.CopyCount > 1) parts.Add($"复制 {Model.CopyCount} 次");
            parts.Add($"{Model.Length} 字符");

            return string.Join(" · ", parts);
        }
    }

    public string TimeText => FormatRelative(Model.UpdatedAt);

    /// <summary>内容被去重命中刷新后同步显示字段。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(IsStarred));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(TimeText));
    }

    /// <summary>只刷新相对时间，供每分钟的定时器调用。</summary>
    public void RefreshTime() => OnPropertyChanged(nameof(TimeText));

    public void MarkPinned(bool pinned)
    {
        Model.IsPinned = pinned;
        OnPropertyChanged(nameof(IsPinned));
    }

    public void MarkStarred(bool starred)
    {
        Model.IsStarred = starred;
        OnPropertyChanged(nameof(IsStarred));
    }

    private static string FormatRelative(DateTimeOffset time)
    {
        var now = DateTimeOffset.Now;
        var span = now - time;

        if (span < TimeSpan.Zero) return time.ToString("HH:mm");
        if (span.TotalSeconds < 60) return "刚刚";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";

        if (time.Date == now.Date) return time.ToString("HH:mm");
        if (time.Date == now.Date.AddDays(-1)) return $"昨天 {time:HH:mm}";
        if (time.Year == now.Year) return time.ToString("M月d日 HH:mm");

        return time.ToString("yyyy年M月d日");
    }
}
