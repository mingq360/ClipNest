using System.Runtime.InteropServices;
using System.Text;
using ClipNest.Infrastructure.Diagnostics;

namespace ClipNest.Infrastructure.Native;

internal static class NativeMethods
{
    public const int WM_CLIPBOARDUPDATE = 0x031D;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_SETTINGCHANGE = 0x001A;

    /// <summary>WM_SETTINGCHANGE 的 lParam 指向区域名字符串。</summary>
    public static string GetSettingChangeArea(IntPtr lParam)
        => lParam == IntPtr.Zero ? string.Empty : Marshal.PtrToStringUni(lParam) ?? string.Empty;

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    public const int MOD_ALT = 0x0001;
    public const int MOD_CONTROL = 0x0002;
    public const int MOD_SHIFT = 0x0004;
    public const int MOD_WIN = 0x0008;
    public const int MOD_NOREPEAT = 0x4000;

    public const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    /// <summary>
    /// 把指定窗口切到前台。
    ///
    /// 直接调 <see cref="SetForegroundWindow"/> 经常会被系统的前台锁拒绝（返回 false），
    /// 所以失败时把本线程的输入队列挂到目标线程上再抢一次 —— 这是跨进程夺焦点的标准做法。
    /// </summary>
    public static bool TryActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (GetForegroundWindow() == hwnd) return true;

        if (SetForegroundWindow(hwnd)) return true;

        var targetThread = GetWindowThreadProcessId(hwnd, out _);
        var ourThread = GetCurrentThreadId();
        if (targetThread == 0 || targetThread == ourThread) return false;

        var attached = false;
        try
        {
            attached = AttachThreadInput(ourThread, targetThread, true);
            return SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(ourThread, targetThread, false);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hwnd);

    public static uint GetWindowProcessId(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;

        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    // ---------------------------------------------------------------- 模拟按键

    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    /// <summary>
    /// 只为占位：INPUT 里的 union 有多大由它决定（x64 为 32 字节）。
    /// 少了它，INPUT 会被排成 32 字节而不是 40，SendInput 会直接返回 0 什么都不发。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;

        // 两个分支从同一偏移开始重叠，和 C 里的 union 一致
        [FieldOffset(8)] public MOUSEINPUT mi;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    /// <summary>
    /// 往当前前台窗口发一次 Ctrl+V。
    /// 用 SendInput 而不是往子窗口发 WM_PASTE：浏览器、Electron 应用只认真实的键盘消息。
    /// </summary>
    public static bool SendCtrlV()
    {
        var inputs = new[]
        {
            MakeKey(VkControl, 0),
            MakeKey(VkV, 0),
            MakeKey(VkV, KeyEventKeyUp),
            MakeKey(VkControl, KeyEventKeyUp),
        };

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != inputs.Length)
        {
            Log.Warn($"SendInput 只送出 {sent}/{inputs.Length} 个事件，错误码 {Marshal.GetLastWin32Error()}");
            return false;
        }

        return true;
    }

    private static INPUT MakeKey(ushort virtualKey, uint flags) => new()
    {
        type = InputKeyboard,
        ki = new KEYBDINPUT
        {
            wVk = virtualKey,
            wScan = 0,
            dwFlags = flags,
            time = 0,
            dwExtraInfo = IntPtr.Zero,
        },
    };

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetWindowTextLength(IntPtr hwnd);

    public delegate void WinEventDelegate(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWinEventHook(
        uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;

        var buffer = new StringBuilder(length + 1);
        _ = GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    // ---------------------------------------------------------------- 屏幕

    public const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>取鼠标所在显示器的工作区（已排除任务栏），单位：物理像素。</summary>
    public static RECT GetCursorWorkArea()
    {
        if (!GetCursorPos(out var cursor))
        {
            return default;
        }

        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return default;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info) ? info.rcWork : default;
    }

    public static POINT GetCursorPosition()
        => GetCursorPos(out var point) ? point : default;

    /// <summary>取窗口所属进程名（不含 .exe，小写）。失败返回 null。</summary>
    public static string? GetProcessName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        try
        {
            _ = GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return null;

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName.ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            // 进程已退出
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
