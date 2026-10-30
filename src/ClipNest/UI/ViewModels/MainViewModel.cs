using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using ClipNest.Domain;
using ClipNest.Infrastructure.Clipboard;
using ClipNest.Infrastructure.Diagnostics;
using ClipNest.Services;
using ClipNest.UI.Infrastructure;
using ClipNest.UI.Localization;

namespace ClipNest.UI.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 500;

    private static readonly (string Key, ClipType? Type)[] Filters =
    {
        ("Str_FilterAll", null),
        ("Str_FilterText", ClipType.Text),
        ("Str_FilterLink", ClipType.Link),
        ("Str_FilterEmail", ClipType.Email),
        ("Str_FilterNumber", ClipType.Number),
    };

    private readonly ClipService _clipService;
    private readonly IClipRepository _repository;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _timeTicker;
    private readonly LocalizationService? _localization;

    private string _searchText = string.Empty;
    private int _selectedFilterIndex;
    private ClipRow? _selectedRow;
    private string _editContent = string.Empty;
    private bool _isDirty;
    private bool _isPaused;
    private string _statusText = string.Empty;
    private string _emptyHint = string.Empty;
    private string? _notice;

    public MainViewModel(ClipService clipService, LocalizationService? localization = null)
    {
        _clipService = clipService;
        _repository = clipService.Repository;
        _localization = localization;

        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            Reload();
        };

        // 相对时间（"3 分钟前"）需要周期性刷新，否则窗口开着不动就会显示旧值
        _timeTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _timeTicker.Tick += (_, _) =>
        {
            foreach (var row in Items) row.RefreshTime();
        };
        _timeTicker.Start();

        RefreshCultureStrings();

        CopyCommand = new RelayCommand(CopySelected, () => SelectedRow is not null);
        TogglePinCommand = new RelayCommand(TogglePin, () => SelectedRow is not null);
        ToggleStarCommand = new RelayCommand(ToggleStar, () => SelectedRow is not null);
        DeleteCommand = new RelayCommand(DeleteSelected, () => SelectedRow is not null);
        DeleteRowCommand = new RelayCommand<ClipRow>(DeleteRow);
        TogglePauseCommand = new RelayCommand(TogglePause);
        SaveCommand = new RelayCommand(Save, () => IsDirty);

        _clipService.ClipSaved += OnClipSaved;
        _clipService.Pruned += OnPruned;
        _clipService.PausedChanged += OnPausedChanged;
        IsPaused = _clipService.IsPaused;

        if (_localization is not null)
        {
            _localization.Changed += OnLanguageChanged;
        }

        Reload();
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshCultureStrings();

    /// <summary>语言切换时刷新由代码维护的字符串（筛选标签、暂停按钮、状态栏等）。</summary>
    private void RefreshCultureStrings()
    {
        FilterLabels = Filters.Select(f => Loc.Get(f.Key)).ToArray();
        OnPropertyChanged(nameof(FilterLabels));
        OnPropertyChanged(nameof(PauseLabel));
        UpdateStatus();
    }

    public ObservableCollection<ClipRow> Items { get; } = new();

    public string[] FilterLabels { get; private set; } = Array.Empty<string>();

    public RelayCommand CopyCommand { get; }

    public RelayCommand TogglePinCommand { get; }

    public RelayCommand ToggleStarCommand { get; }

    public RelayCommand DeleteCommand { get; }

    /// <summary>列表项上的「按行删除」按钮使用，删除指定行而非当前选中行。</summary>
    public RelayCommand<ClipRow> DeleteRowCommand { get; }

    public RelayCommand TogglePauseCommand { get; }

    /// <summary>详情页「保存」：把编辑后的内容写回该片段，并同步系统剪贴板。</summary>
    public RelayCommand SaveCommand { get; }

    /// <summary>复制完成后请求隐藏窗口（由窗口订阅）。</summary>
    public event EventHandler? HideRequested;

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;

            _searchDebounce.Stop();
            _searchDebounce.Start();
        }
    }

    public int SelectedFilterIndex
    {
        get => _selectedFilterIndex;
        set
        {
            if (!SetProperty(ref _selectedFilterIndex, value)) return;
            Reload();
        }
    }

    public ClipRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;

            OnPropertyChanged(nameof(SelectedContent));
            OnPropertyChanged(nameof(HasSelection));

            // 载入选中片段的原文作为可编辑副本；切换片段会丢弃未保存的修改
            EditContent = value?.Content ?? string.Empty;
            IsDirty = false;
        }
    }

    public string SelectedContent => SelectedRow?.Content ?? string.Empty;

    /// <summary>详情页正在编辑的副本。TwoWay 绑定到文本框，保存时才写回。</summary>
    public string EditContent
    {
        get => _editContent;
        set
        {
            if (!SetProperty(ref _editContent, value)) return;
            UpdateDirty();
            // 让 SaveCommand 的 CanExecute（依赖 IsDirty）立即刷新
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>当前编辑内容是否与原内容不同（决定是否可保存）。</summary>
    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    private void UpdateDirty() =>
        IsDirty = HasSelection && EditContent != (SelectedRow?.Content ?? string.Empty);

    public bool HasSelection => SelectedRow is not null;

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (SetProperty(ref _isPaused, value)) OnPropertyChanged(nameof(PauseLabel));
        }
    }

    public string PauseLabel => IsPaused ? Loc.Get("Str_TooltipResume") : Loc.Get("Str_TooltipPause");

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    /// <summary>启动/保存设置时的问题提示（如热键冲突），会顶到状态栏里。</summary>
    public string? Notice
    {
        get => _notice;
        set
        {
            if (SetProperty(ref _notice, value)) UpdateStatus();
        }
    }

    public string EmptyHint
    {
        get => _emptyHint;
        private set => SetProperty(ref _emptyHint, value);
    }

    public bool IsEmpty => Items.Count == 0;

    public void TogglePause() => _clipService.SetPaused(!_clipService.IsPaused);

    public void CopySelected()
    {
        var row = SelectedRow;
        if (row is null) return;

        if (!ClipboardWriter.TrySetText(row.Content))
        {
            StatusText = Loc.Get("Str_CopyFailed");
            return;
        }

        // 从历史里再取一次也计入「使用」，下次打开时它会排在最前
        try
        {
            row.Model.UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _repository.Upsert(row.Model);
        }
        catch (Exception ex)
        {
            Log.Error("更新复制次数失败", ex);
        }

        HideRequested?.Invoke(this, EventArgs.Empty);
    }

    public void TogglePin()
    {
        var row = SelectedRow;
        if (row is null) return;

        var pinned = !row.IsPinned;
        _repository.SetPinned(row.Id, pinned);
        row.MarkPinned(pinned);

        // 置顶影响排序，重查一次让它归位
        Reload(keepSelectionId: row.Id);
    }

    public void ToggleStar()
    {
        var row = SelectedRow;
        if (row is null) return;

        var starred = !row.IsStarred;
        _repository.SetStarred(row.Id, starred);
        row.MarkStarred(starred);
    }

    public void DeleteSelected()
    {
        var row = SelectedRow;
        if (row is null) return;

        var index = Items.IndexOf(row);
        _repository.Delete(row.Id);
        Items.Remove(row);
        SelectedRow = Items.Count == 0 ? null : Items[Math.Clamp(index, 0, Items.Count - 1)];
        UpdateStatus();
    }

    /// <summary>
    /// 保存详情页的编辑：写回数据库、更新内存模型（复制/张贴立即用新内容），
    /// 并同步系统剪贴板。写入走 ClipboardWriter，回声抑制会避免被当成新片段重新收录。
    ///
    /// 改出的内容若与历史里<b>另一条</b>完全相同，保存会被拒绝并提示：
    /// 指纹唯一索引不允许两个相同内容的条目共存。
    /// </summary>
    public void Save()
    {
        var row = SelectedRow;
        if (row is null || !IsDirty) return;

        var newContent = EditContent;

        // 内容相同就意味着指纹相同，会撞上 content_hash 唯一索引。
        // 提前查一次并给出明确原因，比让用户对着一句「保存失败」发呆好。
        try
        {
            var hash = ContentFingerprint.Compute(newContent);
            var existing = _repository.FindByHash(hash);

            if (existing is not null && existing.Id != row.Id)
            {
                StatusText = Loc.Get("Str_SaveDuplicate");
                return;
            }

            if (!_repository.UpdateContent(row.Id, newContent))
            {
                StatusText = Loc.Get("Str_SaveFailed");
                return;
            }

            row.UpdateContent(newContent);

            if (!ClipboardWriter.TrySetText(newContent))
                Log.Warn("保存后写入剪贴板失败（不影响已入库内容）");
        }
        catch (Exception ex)
        {
            Log.Error("保存编辑内容失败", ex);
            StatusText = Loc.Get("Str_SaveFailed");
            return;
        }

        IsDirty = false;
        StatusText = Loc.Get("Str_Saved");
    }

    /// <summary>列表项「按行删除」：删除指定片段，若恰是当前选中项则重新选定相邻项。</summary>
    private void DeleteRow(ClipRow? row)
    {
        if (row is null) return;

        var index = Items.IndexOf(row);
        _repository.Delete(row.Id);
        Items.Remove(row);

        if (ReferenceEquals(SelectedRow, row))
            SelectedRow = Items.Count == 0 ? null : Items[Math.Clamp(index, 0, Items.Count - 1)];

        UpdateStatus();
    }

    /// <summary>清空，保留置顶与收藏。</summary>
    public int ClearUnprotected()
    {
        var removed = _repository.Clear(keepProtected: true);
        Reload();
        return removed;
    }

    public void Reload(long? keepSelectionId = null)
    {
        var keepId = keepSelectionId ?? SelectedRow?.Id;

        try
        {
            var rows = _repository.Query(new ClipQuery
            {
                Text = SearchText,
                Type = Filters[SelectedFilterIndex].Type,
                Limit = PageSize,
            });

            Items.Clear();
            foreach (var clip in rows) Items.Add(new ClipRow(clip));
        }
        catch (Exception ex)
        {
            Log.Error("查询片段失败", ex);
            StatusText = Loc.Get("Str_ReadFailed");
        }

        SelectedRow = keepId is null
            ? Items.FirstOrDefault()
            : Items.FirstOrDefault(r => r.Id == keepId) ?? Items.FirstOrDefault();

        UpdateStatus();
    }

    public void RefreshStatusOnly() => UpdateStatus();

    private void UpdateStatus()
    {
        var total = 0;
        try
        {
            total = _repository.Count();
        }
        catch (Exception ex)
        {
            Log.Error("统计片段数量失败", ex);
        }

        var parts = new List<string>(3) { Loc.Format("Str_StatusTotal", total) };
        if (total > Items.Count && string.IsNullOrEmpty(SearchText)) parts.Add(Loc.Format("Str_StatusShown", Items.Count));
        if (IsPaused) parts.Add(Loc.Get("Str_StatusPaused"));
        if (!string.IsNullOrWhiteSpace(Notice)) parts.Add(Notice!);

        StatusText = string.Join(" · ", parts);

        EmptyHint = string.IsNullOrWhiteSpace(SearchText)
            ? (IsPaused ? Loc.Get("Str_EmptyPaused") : Loc.Get("Str_EmptyNone"))
            : Loc.Format("Str_EmptyNoMatch", SearchText.Trim());

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasSelection));
    }

    private void OnClipSaved(object? sender, Clip clip)
    {
        // 有筛选条件时新片段未必命中，直接重查更省心
        if (!string.IsNullOrWhiteSpace(SearchText) || SelectedFilterIndex != 0)
        {
            Reload();
            return;
        }

        var existing = Items.FirstOrDefault(r => r.Id == clip.Id);
        if (existing is not null)
        {
            // 去重命中：原地刷新并挪到最前
            existing.Refresh();
            var index = Items.IndexOf(existing);
            if (index > 0) Items.Move(index, 0);
            UpdateStatus();
            return;
        }

        Items.Insert(0, new ClipRow(clip));
        while (Items.Count > PageSize) Items.RemoveAt(Items.Count - 1);

        SelectedRow ??= Items[0];
        UpdateStatus();
    }

    private void OnPruned(object? sender, int removed)
    {
        if (removed <= 0) return;
        Reload();
    }

    private void OnPausedChanged(object? sender, bool paused)
    {
        IsPaused = paused;
        UpdateStatus();
    }

    public void Dispose()
    {
        _searchDebounce.Stop();
        _timeTicker.Stop();
        _clipService.ClipSaved -= OnClipSaved;
        _clipService.Pruned -= OnPruned;
        _clipService.PausedChanged -= OnPausedChanged;

        // 语言服务比视图模型活得久，不退订会把整个视图模型连同列表拖到进程结束
        if (_localization is not null) _localization.Changed -= OnLanguageChanged;
    }
}
