using System.Windows.Interop;

namespace ClipNest.Infrastructure.Native;

/// <summary>
/// 隐藏消息窗口：剪贴板监听与全局热键都挂在同一个窗口上，
/// 由 WPF 的 Dispatcher 泵驱动，不额外起线程。
/// 刻意不用 HWND_MESSAGE 消息专用窗口 —— 全局热键挂在消息专用窗口上行为不稳定。
/// </summary>
public sealed class HiddenWindow : IDisposable
{
    private readonly HwndSource _source;
    private bool _disposed;

    /// <summary>返回 true 表示消息已被处理，不再向下传递。</summary>
    public event Func<int, IntPtr, IntPtr, bool>? MessageReceived;

    public IntPtr Handle => _source.Handle;

    public HiddenWindow()
    {
        var parameters = new HwndSourceParameters("ClipNest.HiddenWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,      // 无边框、无标题
            ExtendedWindowStyle = 0x00000080, // WS_EX_TOOLWINDOW：不出现在 Alt+Tab 与任务栏
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var subscribers = MessageReceived;
        if (subscribers is null) return IntPtr.Zero;

        // 逐个调用并对单个订阅者的异常做隔离，避免一个处理失败连带漏掉其他消息处理
        foreach (var subscriber in subscribers.GetInvocationList())
        {
            try
            {
                if (((Func<int, IntPtr, IntPtr, bool>)subscriber)(msg, wParam, lParam))
                {
                    handled = true;
                    break;
                }
            }
            catch (Exception ex)
            {
                Infrastructure.Diagnostics.Log.Error("隐藏窗口消息处理失败", ex);
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
