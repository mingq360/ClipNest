using System.Runtime.InteropServices;
using System.Windows;
using ClipNest.Infrastructure.Diagnostics;
using ClipNest.Infrastructure.Native;

namespace ClipNest.Infrastructure.Clipboard;

public sealed class TextCapturedEventArgs : EventArgs
{
    public TextCapturedEventArgs(string text, ForegroundSnapshot source)
    {
        Text = text;
        Source = source;
    }

    public string Text { get; }

    public ForegroundSnapshot Source { get; }
}

/// <summary>
/// 剪贴板监听。用 AddClipboardFormatListener + WM_CLIPBOARDUPDATE，
/// 不轮询，空闲时零 CPU。
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(120);

    private readonly HiddenWindow _window;
    private readonly ForegroundTracker _tracker;

    private bool _listening;
    private bool _disposed;
    private int _retryCount;
    private ForegroundSnapshot _retrySource = ForegroundSnapshot.Empty;
    private System.Windows.Threading.DispatcherTimer? _retryTimer;

    public ClipboardMonitor(HiddenWindow window, ForegroundTracker tracker)
    {
        _window = window;
        _tracker = tracker;
        _window.MessageReceived += OnMessage;
    }

    /// <summary>读到一段非空文本时触发。</summary>
    public event EventHandler<TextCapturedEventArgs>? TextCaptured;

    public bool IsListening => _listening;

    public bool Start()
    {
        if (_listening) return true;

        _listening = NativeMethods.AddClipboardFormatListener(_window.Handle);
        if (!_listening)
        {
            Log.Error("剪贴板监听注册失败");
        }

        return _listening;
    }

    public void Stop()
    {
        if (!_listening) return;

        _ = NativeMethods.RemoveClipboardFormatListener(_window.Handle);
        _listening = false;
        CancelRetry();
    }

    private bool OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != NativeMethods.WM_CLIPBOARDUPDATE) return false;

        // 事件到达的瞬间就固定来源，避免处理过程中用户又切了窗口
        var source = _tracker.Current;
        _retryCount = 0;
        _retrySource = source;
        TryRead(source);

        return true;
    }

    private void TryRead(ForegroundSnapshot source)
    {
        string? text;
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                // 图片/文件/空剪贴板：首期只收纯文本
                return;
            }

            text = System.Windows.Clipboard.GetText();
        }
        catch (COMException ex)
        {
            // 剪贴板被其他程序独占，稍后重试
            Log.Warn($"读取剪贴板失败（第 {_retryCount + 1} 次）：{ex.Message}");
            ScheduleRetry();
            return;
        }
        catch (ExternalException ex)
        {
            Log.Warn($"读取剪贴板失败（第 {_retryCount + 1} 次）：{ex.Message}");
            ScheduleRetry();
            return;
        }

        if (string.IsNullOrEmpty(text)) return;

        try
        {
            TextCaptured?.Invoke(this, new TextCapturedEventArgs(text, source));
        }
        catch (Exception ex)
        {
            Log.Error("剪贴板内容处理失败", ex);
        }
    }

    private void ScheduleRetry()
    {
        if (_retryCount >= MaxRetries)
        {
            Log.Warn($"剪贴板读取重试 {MaxRetries} 次仍失败，本次放弃");
            CancelRetry();
            return;
        }

        _retryCount++;

        _retryTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = RetryDelay,
        };
        _retryTimer.Tick -= OnRetryTick;
        _retryTimer.Tick += OnRetryTick;
        _retryTimer.Start();
    }

    private void OnRetryTick(object? sender, EventArgs e)
    {
        CancelRetry();
        TryRead(_retrySource);
    }

    private void CancelRetry()
    {
        if (_retryTimer is null) return;

        _retryTimer.Stop();
        _retryTimer.Tick -= OnRetryTick;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _window.MessageReceived -= OnMessage;
    }
}
