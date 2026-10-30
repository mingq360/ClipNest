namespace ClipNest.Services;

/// <summary>
/// 快捷键解析 / 注册失败的原因。
///
/// 只描述「出了什么问题」，不含任何面向用户的文案 ——
/// 文案由界面层按当前语言去取（见 UI.Infrastructure.HotKeyErrorText）。
/// 基础设施层与服务层既不知道界面语言，也不该持有界面文案。
/// </summary>
public enum HotKeyErrorKind
{
    None = 0,

    /// <summary>组合键为空。</summary>
    Empty,

    /// <summary>只有主键、没有修饰键：会把正常打字吃掉，必须拒收。</summary>
    MissingModifier,

    /// <summary>修饰键无法识别。</summary>
    UnknownModifier,

    /// <summary>主键无法识别。</summary>
    UnknownKey,

    /// <summary>主键存在，但无法映射成虚拟键（不能用作全局热键）。</summary>
    NotBindable,

    /// <summary>组合键已被其它程序注册。</summary>
    AlreadyRegistered,

    /// <summary>其它系统错误，详情看 <see cref="HotKeyResult.Win32Error"/>。</summary>
    Win32,
}

/// <summary>解析 / 注册的结果。成功时 <see cref="Kind"/> 为 <see cref="HotKeyErrorKind.None"/>。</summary>
public readonly record struct HotKeyResult(bool Ok, HotKeyErrorKind Kind, string? Detail, int Win32Error)
{
    public static HotKeyResult Success() => new(true, HotKeyErrorKind.None, null, 0);

    /// <summary>
    /// 构造失败结果。
    /// </summary>
    /// <param name="kind">失败原因。</param>
    /// <param name="detail">出错的那个片段（如无法识别的键名），供文案格式化。</param>
    /// <param name="win32Error">系统错误码，仅 <see cref="HotKeyErrorKind.Win32"/> 有意义。</param>
    public static HotKeyResult Fail(HotKeyErrorKind kind, string? detail = null, int win32Error = 0)
        => new(false, kind, detail, win32Error);
}
