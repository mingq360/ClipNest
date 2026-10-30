using System.Collections.ObjectModel;
using System.Windows.Threading;
using ClipNest.Domain;
using ClipNest.Infrastructure.Diagnostics;
using ClipNest.UI.Infrastructure;

namespace ClipNest.UI.ViewModels;

/// <summary>
/// 历史选择菜单的视图模型。
/// 不复用 MainViewModel：那边带着分页、筛选、状态栏等主窗口逻辑，
/// 这里只要「最近 N 条 + 即时搜索 + 单选」，单独一个更清楚。
/// </summary>
public sealed class PickerViewModel : ObservableObject, IDisposable
{
    private const int MaxRows = 60;

    private readonly IClipRepository _repository;
    private readonly DispatcherTimer _debounce;

    private string _searchText = string.Empty;
    private ClipRow? _selectedRow;

    public PickerViewModel(IClipRepository repository)
    {
        _repository = repository;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Reload();
        };

        Reload();
    }

    public ObservableCollection<ClipRow> Items { get; } = new();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;

            _debounce.Stop();
            _debounce.Start();
        }
    }

    public ClipRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!SetProperty(ref _selectedRow, value)) return;

            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedContent));
        }
    }

    public bool HasSelection => _selectedRow is not null;

    public string SelectedContent => _selectedRow?.Content ?? string.Empty;

    public bool IsEmpty => Items.Count == 0;

    /// <summary>
    /// 重新拉取列表。
    /// 失败必须自己兜住：这里被调用的时机是构造期间，异常一冒出去，整个历史菜单都弹不出来。
    /// </summary>
    public void Reload()
    {
        try
        {
            var rows = _repository.Query(new ClipQuery
            {
                Text = SearchText,
                Limit = MaxRows,
            });

            Items.Clear();
            foreach (var clip in rows) Items.Add(new ClipRow(clip));
        }
        catch (Exception ex)
        {
            Log.Error("历史菜单查询片段失败", ex);
            Items.Clear();
        }

        SelectedRow = Items.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>↑↓ 移动选择。</summary>
    public void MoveSelection(int delta)
    {
        if (Items.Count == 0) return;

        var index = SelectedRow is null ? -1 : Items.IndexOf(SelectedRow);
        SelectedRow = Items[Math.Clamp(index + delta, 0, Items.Count - 1)];
    }

    public void Dispose() => _debounce.Stop();
}
