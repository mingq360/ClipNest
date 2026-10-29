using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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

        // 定位做两次：句柄一建好就先挪过去（避免在主屏上闪一下），
        // 布局完成后再定一次（那时尺寸才准，且能覆盖 WPF 自己应用的初始矩形）
        SourceInitialized += (_, _) => MoveToCursor();
        Loaded += (_, _) =>
        {
            MoveToCursor();
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

    /// <summary>
    /// 左上角落在鼠标位置（和右键菜单一致），并夹在鼠标所在的那个显示器里。
    ///
    /// 全程用物理像素，并且一致地用「光标所在显示器」的 DPI，这是多屏下不跑偏的关键：
    /// 窗口在创建的那一刻还挂在主屏上，WPF 的任何 DIP↔像素换算都会按主屏的缩放比去做，
    /// 一旦扩展屏缩放比不同，算出来的位置就会整体偏掉。
    /// </summary>
    private void MoveToCursor()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            var cursor = NativeMethods.GetCursorPosition();
            var (monitor, dpiX, dpiY) = NativeMethods.GetCursorMonitorDpi();
            var workArea = NativeMethods.GetMonitorWorkArea(monitor);

            if (workArea.Width <= 0)
            {
                NativeMethods.MoveWindowPixels(handle, cursor.X, cursor.Y);
                return;
            }

            var x = cursor.X;
            var y = cursor.Y;

            // 高度是内容撑出来的，而且切到另一个缩放比的屏幕后尺寸还会变，
            // 这里按目标屏幕的缩放比预估一下，避免夹取时用了过期的尺寸
            if (NativeMethods.GetWindowRect(handle, out var rect) && rect.Width > 0)
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                var scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1;
                var scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1;

                var width = (int)Math.Round(rect.Width / scaleX * (dpiX / 96.0));
                var height = (int)Math.Round(rect.Height / scaleY * (dpiY / 96.0));

                if (x + width > workArea.Right) x = workArea.Right - width;
                if (y + height > workArea.Bottom) y = workArea.Bottom - height;
            }

            x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - 1));
            y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - 1));

            NativeMethods.MoveWindowPixels(handle, x, y);
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
