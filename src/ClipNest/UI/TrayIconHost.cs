using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Hardcodet.Wpf.TaskbarNotification;
using ClipNest.Infrastructure.Diagnostics;

namespace ClipNest.UI;

/// <summary>托盘图标与右键菜单。</summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuItem _pauseItem;
    private bool _disposed;

    public TrayIconHost(ImageSource? iconSource)
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "ClipNest · 剪贴板历史",
            IconSource = iconSource,
        };

        _pauseItem = new MenuItem { Header = "暂停采集", IsCheckable = true };
        _pauseItem.Click += (_, _) => PauseToggled?.Invoke(this, EventArgs.Empty);

        var showItem = new MenuItem { Header = "显示 ClipNest", FontWeight = FontWeights.SemiBold };
        showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        var settingsItem = new MenuItem { Header = "设置…" };
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);

        var clearItem = new MenuItem { Header = "清空未收藏的片段…" };
        clearItem.Click += (_, _) => ClearRequested?.Invoke(this, EventArgs.Empty);

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _icon.ContextMenu = new ContextMenu
        {
            Items =
            {
                showItem,
                new Separator(),
                _pauseItem,
                settingsItem,
                new Separator(),
                clearItem,
                new Separator(),
                exitItem,
            },
        };

        _icon.TrayLeftMouseUp += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _icon.TrayMouseDoubleClick += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? ShowRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? PauseToggled;

    public event EventHandler? ClearRequested;

    public event EventHandler? ExitRequested;

    public void UpdatePaused(bool paused)
    {
        _pauseItem.IsChecked = paused;
        _icon.ToolTipText = paused ? "ClipNest · 采集已暂停" : "ClipNest · 剪贴板历史";
    }

    public void ShowError(string message, string title = "ClipNest") => ShowBalloon(title, message, BalloonIcon.Warning);

    public void ShowInfo(string message, string title = "ClipNest") => ShowBalloon(title, message, BalloonIcon.Info);

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
