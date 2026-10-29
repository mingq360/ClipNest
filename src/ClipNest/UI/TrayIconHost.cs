using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;
using ClipNest.Infrastructure.Diagnostics;
using ClipNest.UI.Localization;

namespace ClipNest.UI;

/// <summary>托盘图标与右键菜单。</summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _pauseItem;
    private readonly MenuItem _showItem;
    private readonly MenuItem _settingsItem;
    private readonly MenuItem _clearItem;
    private readonly MenuItem _exitItem;
    private readonly LocalizationService? _localization;
    private bool _paused;
    private bool _disposed;

    public TrayIconHost(ImageSource? iconSource, LocalizationService? localization = null)
    {
        _localization = localization;

        _icon = new TaskbarIcon
        {
            ToolTipText = Loc.Get("Str_TrayTooltip"),
            IconSource = iconSource,
        };

        _pauseItem = new MenuItem { IsCheckable = true };
        _pauseItem.Click += (_, _) => PauseToggled?.Invoke(this, EventArgs.Empty);

        _showItem = new MenuItem { FontWeight = FontWeights.SemiBold };
        _showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        _settingsItem = new MenuItem();
        _settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        _clearItem = new MenuItem();
        _clearItem.Click += (_, _) => ClearRequested?.Invoke(this, EventArgs.Empty);

        _exitItem = new MenuItem();
        _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _icon.ContextMenu = new ContextMenu
        {
            Items =
            {
                _showItem,
                new Separator(),
                _pauseItem,
                _settingsItem,
                new Separator(),
                _clearItem,
                new Separator(),
                _exitItem,
            },
        };

        _icon.TrayLeftMouseUp += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _icon.TrayMouseDoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        RefreshStrings();

        if (_localization is not null) _localization.Changed += (_, _) => RefreshStrings();
    }

    /// <summary>根据当前语言刷新菜单项文案（菜单是代码里建的，不走 XAML 的 DynamicResource）。</summary>
    private void RefreshStrings()
    {
        _showItem.Header = Loc.Get("Str_TrayShow");
        _settingsItem.Header = Loc.Get("Str_TraySettings");
        _clearItem.Header = Loc.Get("Str_TrayClear");
        _exitItem.Header = Loc.Get("Str_TrayExit");
        UpdatePaused(_paused);
    }

    public event EventHandler? ShowRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? PauseToggled;

    public event EventHandler? ClearRequested;

    public event EventHandler? ExitRequested;

    public void UpdatePaused(bool paused)
    {
        _paused = paused;
        _pauseItem.IsChecked = paused;
        _pauseItem.Header = Loc.Get(paused ? "Str_TrayResume" : "Str_TrayPause");
        _icon.ToolTipText = paused ? Loc.Get("Str_TrayTooltipPaused") : Loc.Get("Str_TrayTooltip");
    }

    public void ShowError(string message, string title = "") => ShowBalloon(
        string.IsNullOrEmpty(title) ? Loc.Get("Str_AppName") : title, message, BalloonIcon.Warning);

    public void ShowInfo(string message, string title = "") => ShowBalloon(
        string.IsNullOrEmpty(title) ? Loc.Get("Str_AppName") : title, message, BalloonIcon.Info);

    private void ShowBalloon(string title, string message, BalloonIcon icon)
    {
        try
        {
            _icon.ShowBalloonTip(title, message, icon);
        }
        catch (Exception ex)
        {
            // 部分系统禁用了气泡通知，退回日志即可，不要因此崩掉
            Log.Warn($"托盘气泡显示失败：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _icon.Dispose();
    }
}
