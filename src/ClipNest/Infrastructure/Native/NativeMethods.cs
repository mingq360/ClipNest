using System.Collections.Generic;
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

    // ---------------------------------------------------------------- 焦点

    /// <summary>当前线程/前台里真正拿到键盘焦点的那个窗口（通常是一个子控件，不是顶层窗口）。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr GetFocus();

    /// <summary>把键盘焦点交给指定窗口；跨线程前需要 AttachThreadInput。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    // （VkControl / VkV 已在本文件「模拟按键」段定义，这里不重复声明）
    public const int VkMenu = 0x12;
    public const int VkShift = 0x10;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;
    public const int VkSpace = 0x20;
    public const int VkEscape = 0x1B;
    public const int VkReturn = 0x0D;
    public const int VkUp = 0x26;
    public const int VkDown = 0x28;
    public const int VkBack = 0x08;

    /// <summary>判断当前是否有 Ctrl/Alt/Win 之类的修饰键按下（用于把快捷键和真正的输入区分开）。</summary>
    public static bool IsModifierHeld()
    {
        const short down = unchecked((short)0x8000);

        return (GetAsyncKeyState(VkControl) & down) != 0
            || (GetAsyncKeyState(VkMenu) & down) != 0
            || (GetAsyncKeyState(VkLWin) & down) != 0
            || (GetAsyncKeyState(VkRWin) & down) != 0;
    }

    public static bool IsShiftHeld() => (GetAsyncKeyState(VkShift) & unchecked((short)0x8000)) != 0;

    // ---------------------------------------------------------------- 窗口扩展样式

    public const int GwlExStyle = -20;

    /// <summary>显示但不抢焦点：点了也不激活，前台窗口一动不动。</summary>
    public const long WsExNoActivate = 0x08000000L;

    /// <summary>从 Alt+Tab 列表里隐藏，弹出式菜单都该有。</summary>
    public const long WsExToolWindow = 0x00000080L;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

    /// <summary>在样式上按位加/去某一位，返回设置后的值。</summary>
    public static long ModifyExStyle(IntPtr hwnd, long add, long remove = 0)
    {
        if (hwnd == IntPtr.Zero) return 0;

        var current = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        var next = (current | add) & ~remove;

        if (next == current) return current;

        _ = SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(next));
        return next;
    }

    // ---------------------------------------------------------------- 窗口消息

    public const int WmMouseActivate = 0x0021;
    public const int WmNcActivate = 0x0086;

    /// <summary>WM_MOUSEACTIVATE 的返回值：别激活我，也别把焦点拿走。</summary>
    public const int MaNoActivate = 3;

    public const int WmKeyDown = 0x0100;
    public const int WmSysKeyDown = 0x0104;
    public const int WmLButtonDown = 0x0201;
    public const int WmRButtonDown = 0x0204;

    // ---------------------------------------------------------------- 低层钩子

    public const int WhKeyboardLl = 13;
    public const int WhMouseLl = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 装低层钩子（WH_KEYBOARD_LL / WH_MOUSE_LL）。
    /// 这两个钩子是全局的，<b>不用时必须 UnhookWindowsHookEx</b>，否则会一直拖慢系统输入。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

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

    /// <summary>
    /// 补发一次按键（可带 Shift）。
    /// 用途：无激活弹窗在用户敲下第一个字符时才接管键盘，
    /// 那个"触发键"需要原样交还给搜索框，否则用户会丢掉第一个字。
    /// 调用前务必确认自己已经是前台窗口，否则按键会打进别人的文档里。
    /// </summary>
    public static bool SendKeyStroke(ushort vk, bool shift)
    {
        var inputs = new List<INPUT>();
        if (shift) inputs.Add(MakeKey((ushort)VkShift, 0));

        inputs.Add(MakeKey(vk, 0));
        inputs.Add(MakeKey(vk, KeyEventKeyUp));

        if (shift) inputs.Add(MakeKey((ushort)VkShift, KeyEventKeyUp));

        var array = inputs.ToArray();
        var sent = SendInput((uint)array.Length, array, Marshal.SizeOf<INPUT>());

        return sent == (uint)array.Length;
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

    /// <summary>鼠标所在的那个显示器。</summary>
    public static IntPtr GetCursorMonitor()
    {
        if (!GetCursorPos(out var cursor)) return IntPtr.Zero;

        return MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
    }

    /// <summary>取鼠标所在显示器的工作区（已排除任务栏），单位：物理像素。</summary>
    public static RECT GetCursorWorkArea() => GetMonitorWorkArea(GetCursorMonitor());

    /// <summary>取指定窗口所在显示器的工作区，单位：物理像素。</summary>
    public static RECT GetWindowWorkArea(IntPtr hwnd)
        => GetMonitorWorkArea(MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST));

    /// <summary>取某个显示器的工作区。monitor 为 0 时返回空矩形（Width 为 0，调用方可据此判断失败）。</summary>
    public static RECT GetMonitorWorkArea(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return default;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(monitor, ref info) ? info.rcWork : default;
    }

    private const int MdtEffectiveDpi = 0;

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>
    /// 取指定显示器的缩放 DPI（96 为 100%）。
    /// 这是「每个显示器各自不同」的那一档：混合缩放的多屏环境里，主屏和扩展屏往往不一样。
    /// 取不到就按 96 兜底。
    /// </summary>
    public static (uint DpiX, uint DpiY) GetMonitorDpi(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero) return (96, 96);

        return GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out var dpiY) == 0
            ? (dpiX, dpiY)
            : (96, 96);
    }

    /// <summary>鼠标所在显示器的 DPI，顺带把显示器句柄也带出来。</summary>
    public static (IntPtr Monitor, uint DpiX, uint DpiY) GetCursorMonitorDpi()
    {
        var monitor = GetCursorMonitor();
        var (dpiX, dpiY) = GetMonitorDpi(monitor);

        return (monitor, dpiX, dpiY);
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>
    /// 用**物理像素**移动窗口。
    ///
    /// 为什么不直接设 Window.Left/Top：那是 DIP，而 DIP→像素的换算比例是「每显示器」不同的。
    /// 窗口刚创建时还在主屏上，WPF 会用主屏的缩放比去换算目标坐标；
    /// 一旦目标落在缩放比不同的扩展屏上，位置就会偏 —— 多显示器下这是必然出错的地方。
    /// </summary>
    public static bool MoveWindowPixels(IntPtr hwnd, int x, int y)
    {
        if (hwnd == IntPtr.Zero) return false;

        return SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
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
