using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClipNest.Domain;
using ClipNest.Infrastructure.Clipboard;
using ClipNest.Infrastructure.Diagnostics;
using ClipNest.Infrastructure.Native;
using ClipNest.UI.ViewModels;

namespace ClipNest.UI;

/// <summary>
/// 历史选择菜单（CLCL 式）：快捷键唤出 → 选中一条 → 粘贴回原来正在输入的窗口。
///
/// <para><b>两种显示模式</b>（这是"不丢焦点"的关键）：</para>
/// <list type="bullet">
///   <item>
///     <b>无激活模式（默认）</b>：窗口带 <c>WS_EX_NOACTIVATE</c> 显示，点它也不激活，
///     目标窗口从头到尾都是前台 —— 光标、选区、输入法状态一动不动。
///     键盘靠 <c>WH_KEYBOARD_LL</c> 钩子接管（↑↓/Enter/Esc），鼠标点选照常工作。
///     此时粘贴只需直接发 <c>Ctrl+V</c>，不需要抢焦点，也不存在切换前台的时间竞态。
///   </item>
///   <item>
///     <b>输入模式</b>：用户敲下第一个字符、需要在搜索框里打字时才真正激活窗口
///     （此时才不可避免地抢焦点）。退出时把焦点精确还给原来的那个子控件，
///     而不是靠 Windows 大概恢复。
///   </item>
/// </list>
/// </summary>
public partial class PastePopupWindow : Window
{
    private readonly PickerViewModel _viewModel;
    private readonly IClipRepository _repository;

    private readonly IntPtr _targetWindow;

    /// <summary>弹出前真正拿着键盘焦点的那个子控件（不是顶层窗口），退出时还给它。</summary>
    private readonly IntPtr _targetFocus;

    private readonly uint _targetThread;

    private bool _closing;

    /// <summary>true = 已接管键盘（抢过焦点，退出时必须还原）。</summary>
    private bool _inputMode;

    /// <summary>钩子装不上时的兜底：退化成普通弹窗，行为与旧版一致。</summary>
    private bool _forceInputMode;

    private bool _focusRestored;

    // 钩子委托必须长期持有：传给 SetWindowsHookEx 的委托一旦被 GC 回收，回调就是野指针
    private readonly NativeMethods.HookProc _keyboardProc;
    private readonly NativeMethods.HookProc _mouseProc;

    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private HwndSource? _source;

    public PastePopupWindow(
        IClipRepository repository,
        IntPtr targetWindow,
        IntPtr targetFocus,
        uint targetThread)
    {
        InitializeComponent();

        _repository = repository;
        _targetWindow = targetWindow;
        _targetFocus = targetFocus;
        _targetThread = targetThread;

        _viewModel = new PickerViewModel(repository);
        DataContext = _viewModel;

        _keyboardProc = KeyboardHookProc;
        _mouseProc = MouseHookProc;

        // 定位做两次：句柄一建好就先挪过去（避免在主屏上闪一下），
        // 布局完成后再定一次（那时尺寸才准，且能覆盖 WPF 自己应用的初始矩形）
        SourceInitialized += (_, _) =>
        {
            MoveToCursor();
            ApplyNoActivate();
            InstallHooks();
        };

        Loaded += (_, _) =>
        {
            MoveToCursor();

            // 无激活模式下刻意不 Activate / 不 Focus：目标窗口保持前台
            if (_inputMode || _forceInputMode) EnterInputMode(0);
        };

        // 只在输入模式下有意义：那时我们才是前台，失焦就意味着用户点去别处了
        Deactivated += OnDeactivated;

        // 用 handledEventsToo 注册：即便 ListBoxItem 把 MouseUp 标成已处理，单击也要能选中并粘贴
        HistoryList.AddHandler(MouseUpEvent, new MouseButtonEventHandler(OnListMouseUp), handledEventsToo: true);

        // 关闭前（此时自己还是前台）把焦点还回去；任何来源的关闭都会走到这里
        Closing += (_, _) =>
        {
            _closing = true;
            RestoreFocusIfNeeded();
        };

        Closed += (_, _) =>
        {
            UninstallHooks();

            if (_source is not null)
            {
                _source.RemoveHook(WndProc);
                _source = null;
            }

            _viewModel.Dispose();
        };
    }

    /// <summary>用户选中了一条并已尝试粘贴。</summary>
    public event EventHandler? Pasted;

    /// <summary>
    /// 重复按快捷键时把窗口提到前面。
    /// 无激活模式下它本来就在最上层，只重新定位；输入模式下才需要真的激活。
    /// </summary>
    public void BringToFront()
    {
        if (_closing) return;

        if (_inputMode) Activate();
        else MoveToCursor();
    }

    // ------------------------------------------------------------ 无激活显示

    /// <summary>加上"显示但不激活"的扩展样式，并接管 WM_MOUSEACTIVATE。</summary>
    private void ApplyNoActivate()
    {
        if (_inputMode) return;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        NativeMethods.ModifyExStyle(handle, NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow);

        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 点我也不激活 —— 这是"不抢焦点"的最后一道关口
        if (msg == NativeMethods.WmMouseActivate && !_inputMode)
        {
            handled = true;
            return new IntPtr(NativeMethods.MaNoActivate);
        }

        return IntPtr.Zero;
    }

    private void InstallHooks()
    {
        if (_inputMode) return;

        _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhKeyboardLl, _keyboardProc, IntPtr.Zero, 0);
        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WhMouseLl, _mouseProc, IntPtr.Zero, 0);

        if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
        {
            Log.Warn("低层钩子安装失败，历史菜单退化为普通弹窗（会抢焦点）");
            UninstallHooks();
            _forceInputMode = true;
            return;
        }

        Log.Info("历史菜单以无激活模式显示，目标窗口保持前台");
    }

    private void UninstallHooks()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    /// <summary>转入输入模式：去掉 NOACTIVATE、真正激活、焦点交给搜索框。</summary>
    /// <param name="vk">触发进入输入模式的那个按键；非 0 时原样补发进搜索框。</param>
    private void EnterInputMode(uint vk)
    {
        if (_inputMode) return;

        _inputMode = true;

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            NativeMethods.ModifyExStyle(handle, add: 0, remove: NativeMethods.WsExNoActivate);

        // 交还给系统托管：进入输入模式后由 WPF 的 Deactivated 负责"点外部关闭"
        UninstallHooks();

        Activate();
        SearchBox.Focus();

        if (vk == 0) return;

        // 把那个触发键原样补进搜索框。必须确认自己真的成了前台才发，
        // 否则这一下会打进用户正在输入的文档里。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_closing) return;

            var self = new WindowInteropHelper(this).Handle;
            if (NativeMethods.GetForegroundWindow() != self)
            {
                Log.Warn("首字符未补发：窗口尚未成为前台，避免误输入到目标窗口");
                return;
            }

            _ = NativeMethods.SendKeyStroke((ushort)vk, NativeMethods.IsShiftHeld());
        }), DispatcherPriority.Input);
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_closing && !_inputMode)
        {
            var msg = wParam.ToInt32();

            if (msg == NativeMethods.WmKeyDown || msg == NativeMethods.WmSysKeyDown)
            {
                var vk = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam).vkCode;

                // 带 Ctrl/Alt/Win 的是快捷键（包括唤出本窗口的 Alt+C 本身），一律放行
                if (!NativeMethods.IsModifierHeld())
                {
                    switch (vk)
                    {
                        case NativeMethods.VkUp:
                            Dispatcher.BeginInvoke(new Action(() => MoveSelection(-1)));
                            return (IntPtr)1;

                        case NativeMethods.VkDown:
                            Dispatcher.BeginInvoke(new Action(() => MoveSelection(1)));
                            return (IntPtr)1;

                        case NativeMethods.VkReturn:
                            Dispatcher.BeginInvoke(new Action(Commit));
                            return (IntPtr)1;

                        case NativeMethods.VkEscape:
                            Dispatcher.BeginInvoke(new Action(Cancel));
                            return (IntPtr)1;

                        default:
                            if (IsTypingKey(vk))
                            {
                                // 吞掉：不能让它打进用户的文档
                                Dispatcher.BeginInvoke(new Action(() => EnterInputMode(vk)));
                                return (IntPtr)1;
                            }

                            break;
                    }
                }
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    /// <summary>无激活模式收不到 Deactivated，只能靠鼠标钩子判断"点到了外面"。</summary>
    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && !_closing && !_inputMode)
        {
            var msg = wParam.ToInt32();

            if (msg == NativeMethods.WmLButtonDown || msg == NativeMethods.WmRButtonDown)
            {
                var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                if (!IsInsideWindow(info.pt)) Dispatcher.BeginInvoke(new Action(Cancel));
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static bool IsTypingKey(uint vk)
        => vk is >= 0x30 and <= 0x39        // 数字
        || vk is >= 0x41 and <= 0x5A        // 字母
        || vk == NativeMethods.VkSpace
        || vk == NativeMethods.VkBack
        || vk is >= 0xBA and <= 0xC0        // OEM 标点
        || vk is >= 0xDB and <= 0xF5;

    private bool IsInsideWindow(NativeMethods.POINT pt)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return false;
        if (!NativeMethods.GetWindowRect(handle, out var rect)) return false;

        return pt.X >= rect.Left && pt.X <= rect.Right && pt.Y >= rect.Top && pt.Y <= rect.Bottom;
    }

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

    // ------------------------------------------------------------ 焦点还原

    /// <summary>只有抢过焦点才需要还原；无激活模式下目标窗口从未失去焦点。</summary>
    private void RestoreFocusIfNeeded()
    {
        if (!_inputMode || _focusRestored) return;

        _focusRestored = true;
        RestoreFocus();
    }

    /// <summary>
    /// 把焦点精确还给弹出前那个窗口/控件。
    /// 顺序很关键：<b>趁自己还是前台</b>先切回去，再关自己；
    /// 先 Hide 再 SetForegroundWindow 会被系统的前台锁拒绝。
    /// </summary>
    private void RestoreFocus()
    {
        if (_targetWindow == IntPtr.Zero || !NativeMethods.IsWindow(_targetWindow)) return;
        if (NativeMethods.GetWindowProcessId(_targetWindow) == (uint)Environment.ProcessId) return;

        if (!NativeMethods.TryActivateWindow(_targetWindow))
        {
            Log.Warn($"未能把焦点还给目标窗口 {DescribeWindow(_targetWindow)}");
            return;
        }

        if (_targetFocus == IntPtr.Zero || !NativeMethods.IsWindow(_targetFocus)) return;

        var ourThread = NativeMethods.GetCurrentThreadId();
        var attached = false;

        try
        {
            // SetFocus 跨线程必须先挂输入队列，否则不生效
            if (_targetThread != 0 && _targetThread != ourThread)
                attached = NativeMethods.AttachThreadInput(ourThread, _targetThread, true);

            _ = NativeMethods.SetFocus(_targetFocus);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(ourThread, _targetThread, false);
        }

        Log.Info($"焦点已还给 {DescribeWindow(_targetWindow)}");
    }

    // ------------------------------------------------------------ 交互

    private void OnDeactivated(object? sender, EventArgs e) => Cancel();

    private void MoveSelection(int delta)
    {
        _viewModel.MoveSelection(delta);
        HistoryList.ScrollIntoView(_viewModel.SelectedRow);
    }

    /// <summary>
    /// 取消并关闭。
    /// 必须防重入：关闭过程中还会再收到一次失焦，重复 Close() 会抛
    /// "无法在窗口关闭期间设置可见性"。
    /// </summary>
    private void Cancel()
    {
        if (_closing) return;

        _closing = true;
        RestoreFocusIfNeeded();
        Close();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;

            case Key.Up:
                MoveSelection(-1);
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

    /// <summary>选中项 → 写剪贴板 → 把内容送到目标窗口。</summary>
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
            RestoreFocusIfNeeded();
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
            else if (!_inputMode)
            {
                // 无激活模式：目标窗口从头到尾都是前台，光标和输入法都没动过，
                // 直接发 Ctrl+V 即可 —— 不需要抢焦点，也就没有切换前台的时间竞态
                Hide();
                await Task.Delay(30);

                var sent = NativeMethods.SendCtrlV();
                var foreground = NativeMethods.GetForegroundWindow();

                Log.Info($"无激活模式粘贴：Ctrl+V {(sent ? "已发送" : "失败")}，前台={DescribeWindow(foreground)}");

                if (foreground != _targetWindow)
                    Log.Warn($"前台不是原目标窗口（{DescribeWindow(foreground)}），内容已放进剪贴板，可手动粘贴");
            }
            else
            {
                // 输入模式：抢过焦点，所以先精确还回去再发按键
                RestoreFocus();
                _focusRestored = true;

                Hide();

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
