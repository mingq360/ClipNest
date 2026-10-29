using System.Collections;
using System.Windows;
using ClipNest.Services;

namespace ClipNest.UI.Localization;

/// <summary>
/// 把当前语言的字符串资源写进 <see cref="Application.Resources"/>。
///
/// XAML 里一律用 <c>{DynamicResource Str_xxx}</c> 引用这些字符串，
/// 因此切换语言时不需要重建任何窗口 —— DynamicResource 会自动重算。
/// 代码里则用 <see cref="Loc"/> 读取。
///
/// 机制与 <see cref="ClipNest.UI.Theming.ThemeService"/> 一致：
/// 都是把资源直接写进 Application.Resources，变化的那一下会触发 DynamicResource 重绘。
/// </summary>
public sealed class LocalizationService
{
    private readonly Application _application;
    private Language _current;

    private static readonly Dictionary<Language, Uri> Uris = new()
    {
        // LoadComponent 只接受「相对」pack URI（不带 pack:// 方案前缀）
        [Language.Chinese] = new Uri(
            "/ClipNest;component/UI/Localization/Strings.zh-CN.xaml", UriKind.Relative),
        [Language.English] = new Uri(
            "/ClipNest;component/UI/Localization/Strings.en-US.xaml", UriKind.Relative),
    };

    public LocalizationService(Application application) => _application = application;

    /// <summary>当前生效的语言。</summary>
    public Language Current
    {
        get => _current;
        private set => _current = value;
    }

    /// <summary>语言被切换后触发（资源已写入 Application.Resources）。</summary>
    public event EventHandler? Changed;

    /// <summary>加载指定语言的字典并写进 Application.Resources，覆盖旧值。</summary>
    public void Apply(Language language)
    {
        Current = language;

        var dict = (ResourceDictionary)Application.LoadComponent(Uris[language]);
        foreach (DictionaryEntry entry in dict)
        {
            _application.Resources[entry.Key] = entry.Value;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
