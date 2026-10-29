using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using ClipNest.Infrastructure.Data;
using ClipNest.Infrastructure.Diagnostics;
using ClipNest.Infrastructure.Startup;
using ClipNest.Services;
using ClipNest.UI.Theming;

namespace ClipNest.UI;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly ClipService _clipService;
    private readonly ThemeService _themeService;
    private readonly bool _firstRun;

    /// <summary>装载数据期间不要把「控件变化」当成用户操作，否则一打开就触发预览。</summary>
    private bool _loading;

    public SettingsWindow(SettingsService settings, ClipService clipService, ThemeService themeService, bool firstRun)
    {
        InitializeComponent();

        _settings = settings;
        _clipService = clipService;
        _themeService = themeService;
        _firstRun = firstRun;

        TitleText.Text = firstRun ? "初次设置" : "设置";
        WelcomeCard.Visibility = firstRun ? Visibility.Visible : Visibility.Collapsed;

        LoadFromSettings();

        // 用 KeyDown（冒泡）而不是 PreviewKeyDown：快捷键录制框会先把 Esc 吃掉，
        // 否则用户在录制时按 Esc 会误关整个设置窗口
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !e.Handled) OnCancelClick(this, new RoutedEventArgs());
        };
    }

    /// <summary>保存成功后触发，调用方据此重新注册热键、刷新界面。</summary>
    public event EventHandler? Saved;

    /// <summary>用户清空了数据。</summary>
    public event EventHandler? DataCleared;

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
        }
        finally
        {
            _loading = false;
        }
    }

    // ------------------------------------------------------------ 外观

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

    /// <summary>外观改动立即生效，方便当场看到效果；保存才落盘，取消会还原。</summary>
    private void PreviewAppearance()
    {
        if (_loading) return;
        _themeService.Apply(SelectedTheme, SelectedAccentKey);
    }

    private void OnAppearanceChanged(object sender, RoutedEventArgs e) => PreviewAppearance();

    private void OnAccentSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => PreviewAppearance();

    // ------------------------------------------------------------ 保存

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        HideError();

        if (!int.TryParse(MaxItemsBox.Text.Trim(), out var maxItems)
            || maxItems < AppSettings.MinItemsLimit
            || maxItems > AppSettings.MaxItemsLimit)
        {
            ShowError($"最多保存条数请填 {AppSettings.MinItemsLimit} ~ {AppSettings.MaxItemsLimit:N0} 之间的整数");
            Tabs.SelectedIndex = 0;
            MaxItemsBox.Focus();
            return;
        }

        if (!int.TryParse(MaxCharsBox.Text.Trim(), out var maxChars)
            || maxChars < 1_000
            || maxChars > 5_000_000)
        {
            ShowError("单条内容上限请填 1,000 ~ 5,000,000 之间的整数");
            Tabs.SelectedIndex = 0;
            MaxCharsBox.Focus();
            return;
        }

        var toggleGesture = ToggleHotKeyBox.Gesture?.Trim() ?? string.Empty;
        var manualGesture = ManualHotKeyBox.Gesture?.Trim() ?? string.Empty;

        if (ToggleHotKeyEnabled.IsChecked == true
            && !HotKeyGesture.TryParse(toggleGesture, out _, out _, out var toggleError))
        {
            ShowError($"唤起主窗口的快捷键无效：{toggleError}");
            Tabs.SelectedIndex = 2;
            return;
        }

        if (ManualHotKeyEnabled.IsChecked == true
            && !HotKeyGesture.TryParse(manualGesture, out _, out _, out var captureError))
        {
            ShowError($"主动收藏的快捷键无效：{captureError}");
            Tabs.SelectedIndex = 2;
            return;
        }

        if (ToggleHotKeyEnabled.IsChecked == true
            && ManualHotKeyEnabled.IsChecked == true
            && string.Equals(toggleGesture, manualGesture, StringComparison.OrdinalIgnoreCase))
        {
            ShowError("两个快捷键不能相同");
            Tabs.SelectedIndex = 2;
            return;
        }

        var excluded = ExcludedAppsBox.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var autoStart = AutoStartCheck.IsChecked == true;
        var autoStartApplied = AutoStartService.SetEnabled(autoStart);

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
            s.AutoStart = autoStart;
            s.FirstRunCompleted = true;
            s.Theme = theme;
            s.AccentColor = accentKey;
            s.HotKeys.ToggleWindowEnabled = ToggleHotKeyEnabled.IsChecked == true;
            s.HotKeys.ToggleWindow = toggleGesture;
            s.HotKeys.ManualCaptureEnabled = ManualHotKeyEnabled.IsChecked == true;
            s.HotKeys.ManualCapture = manualGesture;
        });

        _themeService.Apply(theme, accentKey);
        Saved?.Invoke(this, EventArgs.Empty);
        Close();

        if (autoStart && !autoStartApplied)
        {
            MessageBox.Show("开机自启写入注册表失败，其余设置已保存。", "ClipNest",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        // 初次设置不保存就关掉时，至少把「已引导」标记写上，避免每次启动都弹
        if (_firstRun && !_settings.Current.FirstRunCompleted)
        {
            _settings.Update(s => s.FirstRunCompleted = true);
        }

        // 取消要吃掉刚才的预览
        _themeService.Apply(_settings.Current.Theme, _settings.Current.AccentColor);
        Close();
    }

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
            ShowError("打开数据目录失败");
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        var total = _clipService.Repository.Count();
        if (total == 0)
        {
            MessageBox.Show(this, "现在没有任何片段。", "ClipNest", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"将删除除「置顶」和「收藏」之外的全部片段，共 {total} 条中可能有大部分会被删掉。\n\n此操作不可撤销，确定继续吗？",
            "清空未收藏的片段",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK) return;

        var removed = _clipService.Repository.Clear(keepProtected: true);
        Log.Info($"用户手动清空了 {removed} 条片段");
        DataCleared?.Invoke(this, EventArgs.Empty);
        MessageBox.Show(this, $"已删除 {removed} 条片段。", "ClipNest", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private void ShowError(string message) => ErrorText.Text = message;

    private void HideError() => ErrorText.Text = string.Empty;
}
