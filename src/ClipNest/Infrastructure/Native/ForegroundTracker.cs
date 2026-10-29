using ClipNest.Infrastructure.Diagnostics;

namespace ClipNest.Infrastructure.Native;

/// <summary>某一时刻的前台窗口快照。</summary>
public sealed record ForegroundSnapshot(IntPtr Handle, string? ProcessName, string Title, long TimestampMs)
{
    public static readonly ForegroundSnapshot Empty = new(IntPtr.Zero, null, string.Empty, 0);

    public bool IsEmpty => Handle == IntPtr.Zero;
}

/// <summary>
/// 持续跟踪前台窗口。
///
/// 为什么不能等剪贴板事件来了再问 GetForegroundWindow：
/// 用户在密码管理器里 Ctrl+C 之后立刻 Alt+Tab，事件处理时前台已经是别的程序，
/// 白名单就会漏判，密码被静默收录。所以这里用 SetWinEventHook 监听
/// EVENT_SYSTEM_FOREGROUND，在切换发生时就把来源记下来，
/// 剪贴板事件到达时只读快照。
/// </summary>
public sealed class ForegroundTracker : IDisposable
{
    // 必须持有委托实例，否则会被 GC 回收导致回调野指针
    private readonly NativeMethods.WinEventDelegate _callback;
    private readonly object _gate = new();

    private IntPtr _hook;
    private ForegroundSnapshot _current = ForegroundSnapshot.Empty;
    private bool _disposed;

    public ForegroundTracker()
    {
        _callback = OnForegroundChanged;

        _hook = NativeMethods.SetWinEventHook(
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero,
            _callback,
            idProcess: 0,
            idThread: 0,
            NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

        if (_hook == IntPtr.Zero)
        {
            Log.Warn("前台窗口钩子注册失败，来源识别将退化为事件到达时查询");
        }

        Capture(NativeMethods.GetForegroundWindow());
    }

    public ForegroundSnapshot Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    private void OnForegroundChanged(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        try
        {
            if (eventType == NativeMethods.EVENT_SYSTEM_FOREGROUND) Capture(hwnd);
        }
        catch (Exception ex)
        {
            Log.Error("前台窗口跟踪回调异常", ex);
        }
    }

    private void Capture(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var snapshot = new ForegroundSnapshot(
            hwnd,
            NativeMethods.GetProcessName(hwnd),
            NativeMethods.GetWindowTitle(hwnd),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        lock (_gate) _current = snapshot;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWinEvent(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
