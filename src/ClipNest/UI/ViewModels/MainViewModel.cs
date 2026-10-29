using System.Collections.ObjectModel;
using System.Windows.Threading;
using ClipNest.Domain;
using ClipNest.Infrastructure.Clipboard;
using ClipNest.Infrastructure.Diagnostics;
using ClipNest.Services;
using ClipNest.UI.Infrastructure;

namespace ClipNest.UI.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 500;

    private static readonly (string Label, ClipType? Type)[] Filters =
    {
        ("全部类型", null),
        ("文本", ClipType.Text),
        ("链接", ClipType.Link),
        ("邮箱", ClipType.Email),
        ("数字", ClipType.Number),
    };

    private readonly ClipService _clipService;
    private readonly IClipRepository _repository;
    private readonly DispatcherTimer _searchDebounce;
    private readonly DispatcherTimer _timeTicker;

    private string _searchText = string.Empty;
    private int _selectedFilterIndex;
    private ClipRow? _selectedRow;
    private bool _isPaused;
    private string _statusText = string.Empty;
    private string _emptyHint = string.Empty;
    private string? _notice;

    public MainViewModel(ClipService clipService)
    {
        _clipService = clipService;
        _repository = clipService.Repository;

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

        FilterLabels = Filters.Select(f => f.Label).ToArray();

        CopyCommand = new RelayCommand(CopySelected, () => SelectedRow is not null);
        TogglePinCommand = new RelayCommand(TogglePin, () => SelectedRow is not null);
        ToggleStarCommand = new RelayCommand(ToggleStar, () => SelectedRow is not null);
        DeleteCommand = new RelayCommand(DeleteSelected, () => SelectedRow is not null);
        TogglePauseCommand = new RelayCommand(TogglePause);

        _clipService.ClipSaved += OnClipSaved;
        _clipService.Pruned += OnPruned;
        _clipService.PausedChanged += OnPausedChanged;
        IsPaused = _clipService.IsPaused;

        Reload();
    }

    public ObservableCollection<ClipRow> Items { get; } = new();

    public string[] FilterLabels { get; }

    public RelayCommand CopyCommand { get; }

    public RelayCommand TogglePinCommand { get; }

    public RelayCommand ToggleStarCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand TogglePauseCommand { get; }

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
        }
    }

    public string SelectedContent => SelectedRow?.Content ?? string.Empty;

    public bool HasSelection => SelectedRow is not null;

    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            if (SetProperty(ref _isPaused, value)) OnPropertyChanged(nameof(PauseLabel));
        }
    }

    public string PauseLabel => IsPaused ? "恢复采集" : "暂停采集";

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
            StatusText = "写入剪贴板失败，剪贴板可能被其他程序占用";
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
            StatusText = "读取失败，详情见日志";
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

        var parts = new List<string>(3) { $"共 {total} 条" };
        if (total > Items.Count && string.IsNullOrEmpty(SearchText)) parts.Add($"已显示最近 {Items.Count} 条");
        if (IsPaused) parts.Add("采集已暂停");
        if (!string.IsNullOrWhiteSpace(Notice)) parts.Add(Notice!);

        StatusText = string.Join(" · ", parts);

        EmptyHint = string.IsNullOrWhiteSpace(SearchText)
            ? (IsPaused ? "采集已暂停，复制的内容暂时不会被收录" : "还没有收录任何片段。复制一段文字试试。")
            : $"没有找到包含「{SearchText.Trim()}」的片段";

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
    }
}
