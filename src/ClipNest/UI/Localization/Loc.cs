using System.Windows;

namespace ClipNest.UI.Localization;

/// <summary>
/// 代码里读取本地化字符串的静态入口。
///
/// 解析顺序：<see cref="Application.Current"/>.Resources[key]（由
/// <see cref="LocalizationService"/> 维护）→ 找不到时退回 key 本身，
/// 这样即便在非 WPF 上下文（如单元测试）里也不会崩。
/// </summary>
public static class Loc
{
    public static string Get(string key)
    {
        if (Application.Current is { } app && app.Resources.Contains(key))
        {
            return (app.Resources[key] as string) ?? key;
        }

        return key;
    }

    public static string Format(string key, params object?[] args)
    {
        var template = Get(key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }
}
