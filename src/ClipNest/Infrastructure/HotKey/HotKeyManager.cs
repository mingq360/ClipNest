using ClipNest.Services;
using ClipNest.Infrastructure.Diagnostics;
using ClipNest.Infrastructure.Native;

namespace ClipNest.Infrastructure.HotKey;

/// <summary>
/// 全局热键。RegisterHotKey 是**系统级独占**：注册成功后其他程序在本地按同一组合键
/// 就收不到这条快捷键了，所以冲突必须如实反馈给用户，不能静默失败。
/// </summary>
public sealed class HotKeyManager : IDisposable
{
    private readonly HiddenWindow _window;
    private readonly Dictionary<int, Action> _handlers = new();
    private readonly HashSet<int> _registered = new();
    private bool _disposed;

    public HotKeyManager(HiddenWindow window)
    {
        _window = window;
        _window.MessageReceived += OnMessage;
    }

    /// <summary>
    /// 注册一个全局热键。
    /// </summary>
    /// <param name="id">应用内唯一 id。</param>
    /// <param name="gesture">形如 "Ctrl+Alt+V"。</param>
    /// <param name="action">按下时执行。</param>
    /// <param name="error">失败原因（用于在设置界面提示）。</param>
    public bool TryRegister(int id, string gesture, Action action, out string? error)
    {
        Unregister(id);

        if (!HotKeyGesture.TryParse(gesture, out var modifiers, out var virtualKey, out error))
        {
            return false;
        }

        var ok = NativeMethods.RegisterHotKey(
            _window.Handle,
            id,
            modifiers | NativeMethods.MOD_NOREPEAT,
            virtualKey);

        if (!ok)
        {
            var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            error = code == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
                ? $"快捷键 {gesture} 已被其他程序占用，请换一个"
                : $"快捷键 {gesture} 注册失败（错误码 {code}）";

            Log.Warn(error);
            return false;
        }

        _handlers[id] = action;
        _registered.Add(id);
        Log.Info($"已注册全局快捷键 {gesture}（id={id}）");
        return true;
    }

    public void Unregister(int id)
    {
        _handlers.Remove(id);

        if (!_registered.Remove(id)) return;

        _ = NativeMethods.UnregisterHotKey(_window.Handle, id);
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.ToArray()) Unregister(id);
    }

    private bool OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != NativeMethods.WM_HOTKEY) return false;

        var id = wParam.ToInt32();
        if (!_handlers.TryGetValue(id, out var action)) return true;

        try
        {
            action();
        }
        catch (Exception ex)
        {
            Log.Error($"热键 {id} 处理失败", ex);
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        UnregisterAll();
        _window.MessageReceived -= OnMessage;
    }
}
