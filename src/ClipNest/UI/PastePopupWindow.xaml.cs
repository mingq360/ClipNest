using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ClipNest.Domain;
using ClipNest.Infrastructure.Clipboard;
using ClipNest.Infrastructure.Diagnostics;
using ClipNest.Infrastructure.Native;
using ClipNest.UI.ViewModels;

namespace ClipNest.UI;

/// <summary>
/// 历史选择菜单（CLCL 式）：快捷键唤出 → 选中一条 → 自动切回原来的窗口并粘贴。
///
/// 关键点：弹出前先记住目标窗口。我们自己弹窗会抢走前台，
/// 而 ForegroundTracker 带 WINEVENT_SKIPOWNPROCESS，所以记录里保留的仍是那个窗口。
/// </summary>
public partial class PastePopupWindow : Window
{
    private readonly PickerViewModel _viewModel;
    private readonly IClipRepository _repository;
    private readonly IntPtr _targetWindow;

    private bool _closing;

    public PastePopupWindow(IClipRepository repository, IntPtr targetWindow)
    {
        InitializeComponent();

        _repository = repository;
        _targetWindow = targetWindow;
        _viewModel = new PickerViewModel(repository);
        DataContext = _viewModel;

        Loaded += (_, _) => PositionNearCursor();
        Deactivated += OnDeactivated;

        // 任何来源的关闭（我们的 Commit、Esc、外部调用）都先把标志立起来：
        // 关闭过程中还会收到一次失焦，那时再 Close() 会抛异常
        Closing += (_, _) => _closing = true;
        Closed += (_, _) => _viewModel.Dispose();
    }

    /// <summary>用户选中了一条并已尝试粘贴。</summary>
    public event EventHandler? Pasted;

    // ------------------------------------------------------------ 定位

    private void PositionNearCursor()
    {
        try
        {
            var cursor = NativeMethods.GetCursorPosition();
            var workArea = NativeMethods.GetCursorWorkArea();
            if (workArea.Width <= 0) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            var scaleX = dpi.DpiScaleX;
            var scaleY = dpi.DpiScaleY;

            var width = ActualWidth > 0 ? ActualWidth : Width;
            var height = ActualHeight > 0 ? ActualHeight : Height;

            // 出现在鼠标右下，贴边时自动翻到另一侧
            var left = cursor.X / scaleX + 6;
            var top = cursor.Y / scaleY + 6;

            var workLeft = workArea.Left / scaleX;
            var workTop = workArea.Top / scaleY;
            var workRight = workArea.Right / scaleX;
            var workBottom = workArea.Bottom / scaleY;

            if (left + width > workRight) left = cursor.X / scaleX - width - 6;
            if (top + height > workBottom) top = workBottom - height;

            Left = Math.Max(workLeft, left);
            Top = Math.Max(workTop, top);

            Activate();
            SearchBox.Focus();
        }
        catch (Exception ex)
        {
            Log.Error("历史菜单定位失败", ex);
        }
    }

    // ------------------------------------------------------------ 交互

    private void OnDeactivated(object? sender, EventArgs e) => Cancel();

    /// <summary>
    /// 取消并关闭。
    /// 必须防重入：关闭过程中还会再收到一次失焦，重复 Close() 会抛
    /// "无法在窗口关闭期间设置可见性"。
    /// </summary>
    private void Cancel()
    {
        if (_closing) return;

        _closing = true;
        Close();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _viewModel.MoveSelection(1);
                HistoryList.ScrollIntoView(_viewModel.SelectedRow);
                e.Handled = true;
                break;

            case Key.Up:
                _viewModel.MoveSelection(-1);
                HistoryList.ScrollIntoView(_viewModel.SelectedRow);
                e.Handled = true;
                break;

            case Key.Enter:
                e.Handled = true;
                Commit();
                break;

            case Key.Escape:
                e.Handled = true;
                Cancel();
                break;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e) => Commit();

    /// <summary>选中项 → 写剪贴板 → 切回目标窗口 → 发 Ctrl+V。</summary>
    private async void Commit()
    {
        if (_closing) return;

        var row = _viewModel.SelectedRow;
        if (row is null) return;

        _closing = true;

        var content = row.Content;

        // 先让出前台，再把焦点还给原来的窗口，否则 Ctrl+V 会打进我们自己
        Hide();

        if (!ClipboardWriter.TrySetText(content))
        {
            Log.Warn("写入剪贴板失败，已取消粘贴");
            Cancel();
            return;
        }

        MarkUsed(row);

        try
        {
            var targetIsSelf = NativeMethods.GetWindowProcessId(_targetWindow) == (uint)Environment.ProcessId;

            if (targetIsSelf || !NativeMethods.IsWindow(_targetWindow))
            {
                // 目标是我们自己或窗口已没了：只把内容放进剪贴板，不模拟按键，
                // 否则 Ctrl+V 会打进 ClipNest 自己的搜索框
                Log.Info(targetIsSelf
                    ? "目标是 ClipNest 自身，仅复制不粘贴"
                    : "目标窗口已不存在，仅复制");
            }
            else
            {
                _ = NativeMethods.SetForegroundWindow(_targetWindow);

                // 给目标窗口一点时间真正拿到焦点，否则按键会丢
                await Task.Delay(90);
                if (!NativeMethods.SendCtrlV())
                {
                    Log.Warn("模拟 Ctrl+V 失败，内容已放进剪贴板，可手动粘贴");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("粘贴失败", ex);
        }

        Pasted?.Invoke(this, EventArgs.Empty);
        Close();
    }

    /// <summary>用过的记录刷新时间，下次排到前面。</summary>
    private void MarkUsed(ClipRow row)
    {
        try
        {
            row.Model.UpdatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _repository.Upsert(row.Model);
        }
        catch (Exception ex)
        {
            Log.Error("更新使用时间失败", ex);
        }
    }
}
