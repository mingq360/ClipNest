using ClipNest.Services;
using ClipNest.UI.Localization;

namespace ClipNest.UI.Infrastructure;

/// <summary>
/// 把热键的错误码翻译成面向用户的文案。
///
/// 为什么文案要留到界面层才落地：服务层与基础设施层不知道当前语言，
/// 也不该持有界面文案（否则中文 string 会漏进英文界面）。它们只回
/// <see cref="HotKeyErrorKind"/>，由这里按语言取对应的字符串。
/// </summary>
public static class HotKeyErrorText
{
    public static string Describe(HotKeyResult result, string? gesture)
    {
        var combo = gesture ?? string.Empty;

        return result.Kind switch
        {
            HotKeyErrorKind.Empty => Loc.Get("Str_HotkeyErrEmpty"),
            HotKeyErrorKind.MissingModifier => Loc.Get("Str_HotkeyErrModifier"),
            HotKeyErrorKind.UnknownModifier => Loc.Format("Str_HotkeyErrUnknownModifier", result.Detail ?? ""),
            HotKeyErrorKind.UnknownKey => Loc.Format("Str_HotkeyErrUnknownKey", result.Detail ?? ""),
            HotKeyErrorKind.NotBindable => Loc.Format("Str_HotkeyErrNotBindable", result.Detail ?? ""),
            HotKeyErrorKind.AlreadyRegistered => Loc.Format("Str_HotkeyErrTaken", combo),
            HotKeyErrorKind.Win32 => Loc.Format("Str_HotkeyErrWin32", combo, result.Win32Error),
            _ => string.Empty,
        };
    }
}
