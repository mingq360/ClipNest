using ClipNest.Domain;
using ClipNest.UI.Infrastructure;
using ClipNest.UI.Localization;

namespace ClipNest.UI.ViewModels;

/// <summary>
/// 列表行的视图模型。包一层是为了让「相对时间」这类显示文本能就地刷新，
/// 而不必为了时间变化重查数据库。
///
/// 这里承担「剪贴片段 → 界面文本」的翻译：类型名、来源、相对时间都按当前语言取，
/// 所以 Domain 层不需要知道界面语言，也不能在这里写死某种语言的字符串。
/// </summary>
public sealed class ClipRow : ObservableObject
{
    public ClipRow(Clip model) => Model = model;

    public Clip Model { get; }

    public long Id => Model.Id;

    public string Content => Model.Content;

    public ClipType Type => Model.Type;

    public string TypeGlyph => Model.Type switch
    {
        ClipType.Link => Loc.Get("Str_GlyphLink"),
        ClipType.Code => Loc.Get("Str_GlyphCode"),
        ClipType.Email => Loc.Get("Str_GlyphEmail"),
        ClipType.Number => Loc.Get("Str_GlyphNumber"),
        _ => Loc.Get("Str_GlyphText"),
    };

    public string TypeName => Model.Type switch
    {
        ClipType.Link => Loc.Get("Str_TypeLink"),
        ClipType.Code => Loc.Get("Str_TypeCode"),
        ClipType.Email => Loc.Get("Str_TypeEmail"),
        ClipType.Number => Loc.Get("Str_TypeNumber"),
        _ => Loc.Get("Str_TypeText"),
    };

    public bool IsPinned => Model.IsPinned;

    public bool IsStarred => Model.IsStarred;

    public bool HasNote => !string.IsNullOrWhiteSpace(Model.Note);

    public string Preview => Model.Preview();

    public string SourceText => string.IsNullOrWhiteSpace(Model.SourceApp)
        ? Loc.Get("Str_UnknownSource")
        : Model.SourceApp!;

    public string TimeText => FormatRelative(Model.UpdatedAt);

    /// <summary>内容被去重命中刷新后同步显示字段。</summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(IsStarred));
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

    /// <summary>
    /// 详情页编辑保存后同步内存模型与派生显示字段（预览、类型、时间等）。
    ///
    /// 必须连指纹一起刷新：指纹由内容算出来，<see cref="UpdateContent(string)"/> 之后
    /// 若还留着旧指纹，下一次「复制该条」用的 <c>Upsert</c> 会拿着旧指纹去撞唯一索引 —— 撞不上
    /// （库里已是新指纹），于是插入一条内容相同的新记录，历史里凭空多一条重复项。
    /// 类型也要重判：改成 URL 之后角标不能还挂着「文本」。
    /// </summary>
    public void UpdateContent(string content)
    {
        Model.Content = content;
        Model.ContentHash = ContentFingerprint.Compute(content);
        Model.Type = ClipClassifier.Classify(content);

        OnPropertyChanged(nameof(Content));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(Type));
        OnPropertyChanged(nameof(TypeGlyph));
        OnPropertyChanged(nameof(TypeName));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(TimeText));
    }

    private static string FormatRelative(DateTimeOffset time)
    {
        var now = DateTimeOffset.Now;
        var span = now - time;

        if (span < TimeSpan.Zero) return time.ToString("HH:mm");
        if (span.TotalSeconds < 60) return Loc.Get("Str_JustNow");
        if (span.TotalMinutes < 60) return Loc.Format("Str_MinutesAgo", (int)span.TotalMinutes);

        if (time.Date == now.Date) return time.ToString("HH:mm");
        if (time.Date == now.Date.AddDays(-1)) return Loc.Format("Str_Yesterday", time);
        if (time.Year == now.Year) return Loc.Format("Str_ThisYear", time);

        return Loc.Format("Str_FullDate", time);
    }
}
