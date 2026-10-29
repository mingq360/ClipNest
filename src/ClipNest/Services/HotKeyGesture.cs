using System.Windows.Input;

namespace ClipNest.Services;

/// <summary>
/// 全局快捷键的字符串形态与 (修饰键, 虚拟键) 之间的转换。
/// 只接受「至少一个修饰键 + 一个主键」，否则会抢占正常输入。
/// </summary>
public static class HotKeyGesture
{
    public const int ModAlt = 0x0001;
    public const int ModControl = 0x0002;
    public const int ModShift = 0x0004;
    public const int ModWin = 0x0008;

    public static bool TryParse(string? text, out int modifiers, out int virtualKey, out string? error)
    {
        modifiers = 0;
        virtualKey = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "快捷键不能为空";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            error = "全局快捷键至少需要一个修饰键（Ctrl / Alt / Shift / Win）";
            return false;
        }

        var keyName = parts[^1];
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= ModControl;
                    break;
                case "alt":
                    modifiers |= ModAlt;
                    break;
                case "shift":
                    modifiers |= ModShift;
                    break;
                case "win":
                case "windows":
                    modifiers |= ModWin;
                    break;
                default:
                    error = $"无法识别的修饰键：{parts[i]}";
                    return false;
            }
        }

        if (modifiers == 0)
        {
            error = "全局快捷键至少需要一个修饰键（Ctrl / Alt / Shift / Win）";
            return false;
        }

        if (!TryParseKey(keyName, out var key))
        {
            error = $"无法识别的主键：{keyName}";
            return false;
        }

        virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            error = $"按键 {keyName} 不能用作全局快捷键";
            return false;
        }

        return true;
    }

    public static string Format(int modifiers, int virtualKey)
    {
        var parts = new List<string>(4);
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");

        var key = KeyInterop.KeyFromVirtualKey(virtualKey);
        parts.Add(KeyToDisplay(key));
        return string.Join("+", parts);
    }

    public static string KeyToDisplay(Key key)
    {
        // Key.D1 → "1"，其余用枚举名（字母本身就是 "V"、"A"）
        if (key is >= Key.D0 and <= Key.D9) return ((int)key - (int)Key.D0).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return "Num" + ((int)key - (int)Key.NumPad0);

        return key switch
        {
            Key.Oem3 => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.Oem6 => "]",
            Key.Oem5 => "\\",
            Key.Oem1 => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.Space => "Space",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            _ => key.ToString(),
        };
    }

    private static bool TryParseKey(string name, out Key key)
    {
        key = Key.None;
        if (name.Length == 0) return false;

        // 单个数字：Key 枚举里是 D0..D9
        if (name.Length == 1 && char.IsAsciiDigit(name[0]))
        {
            key = Key.D0 + (name[0] - '0');
            return true;
        }

        // 常见别名
        var alias = name.ToLowerInvariant() switch
        {
            "esc" => Key.Escape,
            "enter" or "return" => Key.Return,
            "space" => Key.Space,
            "`" => Key.Oem3,
            "-" => Key.OemMinus,
            "=" => Key.OemPlus,
            "[" => Key.OemOpenBrackets,
            "]" => Key.Oem6,
            "\\" => Key.Oem5,
            ";" => Key.Oem1,
            "'" => Key.OemQuotes,
            "," => Key.OemComma,
            "." => Key.OemPeriod,
            "/" => Key.OemQuestion,
            _ => Key.None,
        };

        if (alias != Key.None)
        {
            key = alias;
            return true;
        }

        return Enum.TryParse(name, ignoreCase: true, out key) && key != Key.None;
    }
}
