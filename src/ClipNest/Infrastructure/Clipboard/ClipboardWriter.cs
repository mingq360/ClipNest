using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using ClipNest.Domain;
using ClipNest.Infrastructure.Diagnostics;

namespace ClipNest.Infrastructure.Clipboard;

/// <summary>
/// 写剪贴板 + 回声抑制。
///
/// 用户点击历史条目时我们会把内容写回剪贴板，这会再触发一次 WM_CLIPBOARDUPDATE。
/// 如果不识别，就会出现「点一下 → 又收录一条 / 复制次数 +1」的死循环噪音。
/// 做法：记住自己写入的指纹，短时间内读到相同内容就当作自己的回声丢弃。
/// </summary>
public static class ClipboardWriter
{
    private static readonly TimeSpan EchoWindow = TimeSpan.FromSeconds(1.5);

    private static string? _lastHash;
    private static long _lastWriteMs;

    public static long WriteCount { get; private set; }

    public static bool TrySetText(string text)
    {
        var hash = ContentFingerprint.Compute(text);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
                _lastHash = hash;
                _lastWriteMs = NowMs();
                WriteCount++;
                return true;
            }
            catch (COMException ex)
            {
                Log.Warn($"写入剪贴板失败（第 {attempt + 1} 次）：{ex.Message}");
                Thread.Sleep(40);
            }
            catch (ExternalException ex)
            {
                Log.Warn($"写入剪贴板失败（第 {attempt + 1} 次）：{ex.Message}");
                Thread.Sleep(40);
            }
        }

        return false;
    }

    /// <summary>判断刚读到的内容是不是我们自己刚写进去的。</summary>
    public static bool IsEchoOfOwnWrite(string text)
    {
        if (_lastHash is null) return false;

        if (NowMs() - _lastWriteMs > EchoWindow.TotalMilliseconds)
        {
            _lastHash = null;
            return false;
        }

        return ContentFingerprint.Compute(text) == _lastHash;
    }

    public static void Reset()
    {
        _lastHash = null;
        _lastWriteMs = 0;
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
