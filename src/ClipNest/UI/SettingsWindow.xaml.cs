using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ClipNest.Infrastructure.Data;
using ClipNest.Infrastructure.Diagnostics;
using ClipNest.Infrastructure.Startup;
using ClipNest.Services;
using ClipNest.UI.Localization;
using ClipNest.UI.Theming;

namespace ClipNest.UI;

/// <summary>
/// 设置窗口。没有「保存 / 取消」—— 任何改动立即写入配置并生效，
/// 关掉窗口就是完成。因此这里不存在"草稿"状态，每个控件都要能独立触发应用。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ClipService _clipService;
    private readonly ThemeService _themeService;
    private readonly LocalizationService _localization;
    private readonly Func<string?>? _applyLive;
    private readonly bool _firstRun;

    /// <summary>装载数据期间不要把「控件变化」当成用户操作。</summary>
    private bool _loading;

    public SettingsWindow(
        SettingsService settings,
        ClipService clipService,
        ThemeService themeService,
        LocalizationService localization,
        bool firstRun,
        Func<string?>? applyLive = null)
    {
        InitializeComponent();

        _settings = settings;
        _clipService = clipService;
        _themeService = themeService;
        _localization = localization;
        _firstRun = firstRun;
        _applyLive = applyLive;

        RefreshLocalizedStrings();
        WelcomeCard.Visibility = firstRun ? Visibility.Visible : Visibility.Collapsed;
        HintSwitchTabOnFirstRun(firstRun);

        LoadFromSettings();
        ShowCurrentPage();

        // 语言切换时刷新代码里写死的标题等（XAML 里的 DynamicResource 会自动更新）
        _localization.Changed += (_, _) => RefreshLocalizedStrings();

        // 用 KeyDown（冒泡）而不是 PreviewKeyDown：快捷键录制框会先把 Esc 吃掉
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled) Close();
        };

        Closed += (_, _) =>
        {
            // 没有保存按钮，关窗即视为完成引导
            if (_firstRun && !_settings.Current.FirstRunCompleted)
            {
                _settings.Update(s => s.FirstRunCompleted = true);
            }
        };
    }

    /// <summary>刷新由代码写死的字符串（标题）。XAML 的 DynamicResource 会自动跟着语言变。</summary>
    private void RefreshLocalizedStrings()
    {
        TitleText.Text = _firstRun ? Loc.Get("Str_SettingsFirstRun") : Loc.Get("Str_Settings");
    }

    /// <summary>用户清空了数据。</summary>
    public event EventHandler? DataCleared;

    private void HintSwitchTabOnFirstRun(bool firstRun)
    {
        if (firstRun) NavList.SelectedIndex = 2; // 首次启动先看快捷键
    }

    /// <summary>左栏导航切换：只显示当前分页，其余收起（无滚动条，整页平铺）。</summary>
    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowCurrentPage();

    private void ShowCurrentPage()
    {
        // XAML 里给 NavList 设 SelectedIndex="0" 会在 InitializeComponent 期间就触发本方法，
        // 此时后面的分页面板字段还没赋值（仍为 null），先跳过；ctor 末尾会再调一次补全。
        if (PageCapture is null || PagePrivacy is null || PageHotkeys is null
            || PageAppearance is null || PageOther is null) return;

        var index = NavList.SelectedIndex;
        PageCapture.Visibility    = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PagePrivacy.Visibility    = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageHotkeys.Visibility    = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageAppearance.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageOther.Visibility      = index == 4 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var current = _settings.Current;

            EnableMonitorCheck.IsChecked = current.EnableMonitor;
            ManualOnlyCheck.IsChecked = current.ManualOnly;
            ExcludedAppsBox.Text = string.Join(Environment.NewLine, current.ExcludedApps);
            MaxItemsBox.Text = current.MaxItems.ToString();
            MaxCharsBox.Text = current.MaxContentChars.ToString();

            ToggleHotKeyBox.Gesture = current.HotKeys.ToggleWindow;
            ToggleHotKeyEnabled.IsChecked = current.HotKeys.ToggleWindowEnabled;
            ManualHotKeyBox.Gesture = current.HotKeys.ManualCapture;
            ManualHotKeyEnabled.IsChecked = current.HotKeys.ManualCaptureEnabled;

            HideOnLostFocusCheck.IsChecked = current.HideOnLostFocus;
            AutoStartCheck.IsChecked = AutoStartService.IsEnabled();
            DataPathText.Text = Db.DataDirectory;

            ThemeLightRadio.IsChecked = current.Theme == ThemeMode.Light;
            ThemeDarkRadio.IsChecked = current.Theme == ThemeMode.Dark;
            ThemeSystemRadio.IsChecked = current.Theme == ThemeMode.System;

            AccentList.ItemsSource = AccentPalettes.All;
            AccentList.SelectedItem = AccentPalettes.Resolve(current.AccentColor);

            LanguageCombo.SelectedIndex = (int)current.Language;
        }
        finally
        {
            _loading = false;
        }
    }

    // ------------------------------------------------------------ 选取值

    private ThemeMode SelectedTheme
    {
        get
        {
            if (ThemeDarkRadio.IsChecked == true) return ThemeMode.Dark;
            if (ThemeSystemRadio.IsChecked == true) return ThemeMode.System;
            return ThemeMode.Light;
        }
    }

    private string SelectedAccentKey
        => (AccentList.SelectedItem as AccentPalette)?.Key ?? AccentPalettes.DefaultKey;

    private string ToggleGesture => ToggleHotKeyBox.Gesture?.Trim() ?? string.Empty;

    private string ManualGesture => ManualHotKeyBox.Gesture?.Trim() ?? string.Empty;

    // ------------------------------------------------------------ 即时应用

    private void OnCaptureSettingChanged(object sender, RoutedEventArgs e) => ApplyLive();

    private void OnAppearanceChanged(object sender, RoutedEventArgs e) => ApplyLive();

    private void OnAccentSelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyLive();

    /// <summary>语言切换立即生效：写盘并重新加载字符串资源（XAML 的 DynamicResource 会自动刷新）。</summary>
    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var lang = LanguageCombo.SelectedIndex == 1
            ? Services.Language.English
            : Services.Language.Chinese;
        _settings.Update(s => s.Language = lang);
        _localization.Apply(lang);
    }

    private void OnHotKeyGestureChanged(object? sender, EventArgs e) => ApplyLive();

    private void OnHotKeyChanged(object sender, RoutedEventArgs e) => ApplyLive();

    private void OnExcludedAppsChanged(object sender, RoutedEventArgs e) => ApplyLive();

    private void OnNumericSettingChanged(object sender, RoutedEventArgs e) => ApplyLive(validateNumbers: true);

    /// <summary>数字框里按 Esc 还原，按回车立即生效。</summary>
    private void OnNumericBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;

        switch (e.Key)
        {
            case Key.Enter:
                box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
                break;
            case Key.Escape:
                box.Text = ReferenceEquals(box, MaxItemsBox)
                    ? _settings.Current.MaxItems.ToString()
                    : _settings.Current.MaxContentChars.ToString();
                HideError();
                e.Handled = true;
                break;
        }
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var wanted = AutoStartCheck.IsChecked == true;
        if (AutoStartService.SetEnabled(wanted))
        {
            ApplyLive();
            return;
        }

        // 写注册表失败就别假装成功
        _loading = true;
        AutoStartCheck.IsChecked = !wanted;
        _loading = false;
        ShowError(Loc.Get("Str_ErrAutoStart"));
    }

    /// <summary>
    /// 把所有控件当前的值落盘并生效。
    /// </summary>
    /// <param name="validateNumbers">只有失焦/回车时才校验数字框，避免边打字边报错。</param>
    private void ApplyLive(bool validateNumbers = false)
    {
        if (_loading) return;

        HideError();

        var maxItems = _settings.Current.MaxItems;
        var maxChars = _settings.Current.MaxContentChars;

        if (validateNumbers)
        {
            if (!TryReadRange(MaxItemsBox, AppSettings.MinItemsLimit, AppSettings.MaxItemsLimit,
                    maxItems, Loc.Get("Str_MaxItems"), out maxItems)) return;

            if (!TryReadRange(MaxCharsBox, 1_000, 5_000_000,
                    maxChars, Loc.Get("Str_MaxCharsLabel"), out maxChars)) return;
        }

        if (ToggleHotKeyEnabled.IsChecked == true
            && !HotKeyGesture.TryParse(ToggleGesture, out _, out _, out var toggleError))
        {
            ShowError(Loc.Format("Str_ErrHotkeyToggle", toggleError));
            return;
        }

        if (ManualHotKeyEnabled.IsChecked == true
            && !HotKeyGesture.TryParse(ManualGesture, out _, out _, out var captureError))
        {
            ShowError(Loc.Format("Str_ErrHotkeyManual", captureError));
            return;
        }

        if (ToggleHotKeyEnabled.IsChecked == true
            && ManualHotKeyEnabled.IsChecked == true
            && string.Equals(ToggleGesture, ManualGesture, StringComparison.OrdinalIgnoreCase))
        {
            ShowError(Loc.Get("Str_ErrHotkeySame"));
            return;
        }

        var excluded = ExcludedAppsBox.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var theme = SelectedTheme;
        var accentKey = SelectedAccentKey;

        _settings.Update(s =>
        {
            s.EnableMonitor = EnableMonitorCheck.IsChecked == true;
            s.ManualOnly = ManualOnlyCheck.IsChecked == true;
            s.ExcludedApps = excluded;
            s.MaxItems = maxItems;
            s.MaxContentChars = maxChars;
            s.HideOnLostFocus = HideOnLostFocusCheck.IsChecked == true;
            s.AutoStart = AutoStartCheck.IsChecked == true;
            s.FirstRunCompleted = true;
            s.Theme = theme;
            s.AccentColor = accentKey;
            s.HotKeys.ToggleWindowEnabled = ToggleHotKeyEnabled.IsChecked == true;
            s.HotKeys.ToggleWindow = ToggleGesture;
            s.HotKeys.ManualCaptureEnabled = ManualHotKeyEnabled.IsChecked == true;
            s.HotKeys.ManualCapture = ManualGesture;
        });

        _themeService.Apply(theme, accentKey);

        // 让主程序重新注册热键、开关监听，并把失败原因显示在当前窗口里
        var error = _applyLive?.Invoke();
        if (!string.IsNullOrEmpty(error)) ShowError(error);
    }

    private bool TryReadRange(TextBox box, int min, int max, int fallback, string label, out int value)
    {
        if (int.TryParse(box.Text.Trim(), out value) && value >= min && value <= max) return true;

        ShowError(Loc.Format("Str_ErrRange", label, min, max, fallback));
        value = fallback;
        box.Text = fallback.ToString();
        return false;
    }

    // ------------------------------------------------------------ 其它动作

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Db.DataDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{Db.DataDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("打开数据目录失败", ex);
            ShowError(Loc.Get("Str_ErrOpenFolder"));
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        var total = _clipService.Repository.Count();
        if (total == 0)
        {
            MessageBox.Show(this, Loc.Get("Str_ClearEmpty"), Loc.Get("Str_ClearTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            Loc.Format("Str_ClearConfirm", total),
            Loc.Get("Str_ClearTitle"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK) return;

        var removed = _clipService.Repository.Clear(keepProtected: true);
        Log.Info($"用户手动清空了 {removed} 条片段");
        DataCleared?.Invoke(this, EventArgs.Empty);
        MessageBox.Show(this, Loc.Format("Str_ClearDone", removed), Loc.Get("Str_ClearTitle"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已抬起
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBar.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorText.Text = string.Empty;
        ErrorBar.Visibility = Visibility.Collapsed;
    }
}
