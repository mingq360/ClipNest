using System.Windows;
using System.Windows.Controls;
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

        // 定位放在 SourceInitialized：这时句柄已建、DPI 已知，窗口还没绘制，
        // 放在 Loaded 里会先出现在默认位置再跳一下
        SourceInitialized += (_, _) => MoveToCursor();
        Loaded += (_, _) =>
        {
            EnsureFitsOnScreen();
            Activate();
            SearchBox.Focus();
        };

        Deactivated += OnDeactivated;

        // 用 handledEventsToo 注册：即便 ListBoxItem 把 MouseUp 标成已处理，单击也要能选中并粘贴
        HistoryList.AddHandler(MouseUpEvent, new MouseButtonEventHandler(OnListMouseUp), handledEventsToo: true);

        // 任何来源的关闭（我们的 Commit、Esc、外部调用）都先把标志立起来：
        // 关闭过程中还会收到一次失焦，那时再 Close() 会抛异常
        Closing += (_, _) => _closing = true;
        Closed += (_, _) => _viewModel.Dispose();
    }

    /// <summary>用户选中了一条并已尝试粘贴。</summary>
    public event EventHandler? Pasted;

    // ------------------------------------------------------------ 定位

    /// <summary>左上角直接落在鼠标位置（和右键菜单一致），只做屏幕内夹取。</summary>
    private void MoveToCursor()
    {
        try
        {
            var cursor = NativeMethods.GetCursorPosition();
            var workArea = NativeMethods.GetCursorWorkArea();
            if (workArea.Width <= 0) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            var x = cursor.X / dpi.DpiScaleX;
            var y = cursor.Y / dpi.DpiScaleY;

            var width = ActualWidth > 0 ? ActualWidth : Width;
            var workLeft = workArea.Left / dpi.DpiScaleX;
            var workRight = workArea.Right / dpi.DpiScaleX;
            var workTop = workArea.Top / dpi.DpiScaleY;

            Left = Math.Max(workLeft, Math.Min(x, workRight - width));
            Top = Math.Max(workTop, y);
        }
        catch (Exception ex)
        {
            Log.Error("历史菜单定位失败", ex);
        }
    }

    /// <summary>高度是内容撑出来的，等布局完成后再纠一次底部越界。</summary>
    private void EnsureFitsOnScreen()
    {
        try
        {
            var workArea = NativeMethods.GetCursorWorkArea();
            if (workArea.Width <= 0 || ActualHeight <= 0) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            var workTop = workArea.Top / dpi.DpiScaleY;
            var workBottom = workArea.Bottom / dpi.DpiScaleY;

            if (Top + ActualHeight <= workBottom) return;

            Top = Math.Max(workTop, workBottom - ActualHeight);
        }
        catch (Exception ex)
        {
            Log.Error("历史菜单边界修正失败", ex);
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

    /// <summary>单击某一条就立刻粘贴，不用再按 Enter。</summary>
    private void OnListMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_closing || e.ChangedButton != MouseButton.Left) return;

        // 点在滚动条或空白处时找不到 ListBoxItem，按 DataContext 判定更稳
        if (e.OriginalSource is not DependencyObject source) return;
        if (FindAncestor<ListBoxItem>(source)?.DataContext is not ClipRow row) return;

        _viewModel.SelectedRow = row;
        e.Handled = true;
        Commit();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T typed) return typed;

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>选中项 → 写剪贴板 → 切回目标窗口 → 发 Ctrl+V。</summary>
    private async void Commit()
    {
        if (_closing) return;

        var row = _viewModel.SelectedRow;
        if (row is null) return;

        _closing = true;
        var content = row.Content;

        if (!ClipboardWriter.TrySetText(content))
        {
            Log.Warn("写入剪贴板失败，未粘贴");
            Close();
            return;
        }

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
                // 顺序很关键：**趁自己还是前台**先把焦点还回去。
                // 先 Hide 再 SetForegroundWindow 会被系统的前台锁拒绝，
                // 结果按键发出去但落在没人接的地方。
                var activated = NativeMethods.TryActivateWindow(_targetWindow);
                Log.Info($"切回目标窗口 {DescribeWindow(_targetWindow)}：{(activated ? "成功" : "失败")}");

                Hide();

                if (!activated)
                {
                    Log.Warn("未能切回目标窗口，内容已放进剪贴板，请手动粘贴");
                }
                else
                {
                    // 等焦点真正落定，太短会把按键丢在切换途中
                    await Task.Delay(120);

                    if (NativeMethods.GetForegroundWindow() != _targetWindow)
                    {
                        // 有些程序切前台是异步的（先激活框、再激活子控件），再给一次机会
                        Log.Warn($"前台仍是 {DescribeWindow(NativeMethods.GetForegroundWindow())}，重试一次切回");
                        _ = NativeMethods.TryActivateWindow(_targetWindow);
                        await Task.Delay(120);
                    }

                    var foreground = NativeMethods.GetForegroundWindow();
                    var sent = NativeMethods.SendCtrlV();
                    Log.Info($"发送 Ctrl+V：{(sent ? "已发送" : "失败")}，发送时前台={DescribeWindow(foreground)}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("粘贴失败", ex);
        }

        MarkUsed(row);

        Pasted?.Invoke(this, EventArgs.Empty);
        Close();
    }

    private static string DescribeWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "0x0";

        var process = NativeMethods.GetProcessName(hwnd) ?? "?";
        var title = NativeMethods.GetWindowTitle(hwnd);
        if (title.Length > 24) title = title[..24] + "…";

        return $"0x{hwnd.ToInt64():X}({process} / {title})";
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
