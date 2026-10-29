using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ClipNest.Domain;
using ClipNest.Infrastructure.Clipboard;
using ClipNest.Infrastructure.Data;
using ClipNest.Infrastructure.Diagnostics;
using ClipNest.Infrastructure.HotKey;
using ClipNest.Infrastructure.Native;
using ClipNest.Services;
using ClipNest.UI;
using ClipNest.UI.ViewModels;
using Microsoft.Data.Sqlite;

namespace ClipNest;

public partial class App : Application
{
    private const string MutexName = "ClipNest.SingleInstance.7f3c";
    private const string PipeName = "ClipNest.SingleInstance.7f3c";
    private const string ShowCommand = "show";

    private const int HotKeyToggleWindow = 1;
    private const int HotKeyManualCapture = 2;

    private Mutex? _mutex;
    private CancellationTokenSource? _pipeCancellation;

    private SettingsService? _settings;
    private Db? _db;
    private SqliteClipRepository? _repository;
    private ClipService? _clipService;

    private HiddenWindow? _hiddenWindow;
    private ForegroundTracker? _foregroundTracker;
    private ClipboardMonitor? _monitor;
    private HotKeyManager? _hotKeys;
    private TrayIconHost? _tray;

    private MainWindow? _mainWindow;
    private MainViewModel? _viewModel;
    private SettingsWindow? _settingsWindow;

    private bool _exiting;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例必须在建库之前判断，否则第二个实例会先动数据库
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            if (!TrySignalRunningInstance())
            {
                Log.Info("已有实例在运行，但未能唤起它");
            }

            Shutdown();
            return;
        }

        RegisterExceptionHandlers();

        try
        {
            Bootstrap();
        }
        catch (Exception ex)
        {
            Log.Error("启动失败", ex);
            MessageBox.Show(
                $"ClipNest 启动失败：{ex.Message}\n\n详情见日志：{Path.Combine(Db.DataDirectory, "logs")}",
                "ClipNest",
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            ExitApplication();
        }
    }

    private void Bootstrap()
    {
        Log.Initialize(Path.Combine(Db.DataDirectory, "logs"));
        Log.Info("==== ClipNest 启动 ====");

        _settings = new SettingsService();
        _settings.Load();

        _db = new Db();
        _db.Initialize();

        _repository = new SqliteClipRepository(_db);
        _clipService = new ClipService(_repository, _settings);

        _hiddenWindow = new HiddenWindow();
        _foregroundTracker = new ForegroundTracker();

        _monitor = new ClipboardMonitor(_hiddenWindow, _foregroundTracker);
        _monitor.TextCaptured += OnTextCaptured;

        _hotKeys = new HotKeyManager(_hiddenWindow);

        _viewModel = new MainViewModel(_clipService);
        _mainWindow = new MainWindow(_viewModel, _settings);
        _mainWindow.SettingsRequested += (_, _) => OpenSettings(firstRun: false);

        _tray = new TrayIconHost(TryFindResource("AppIconImage") as ImageSource);
        _tray.ShowRequested += (_, _) => ShowMainWindow();
        _tray.SettingsRequested += (_, _) => OpenSettings(firstRun: false);
        _tray.PauseToggled += (_, _) => _clipService.TogglePause();
        _tray.ClearRequested += (_, _) => ClearFromTray();
        _tray.ExitRequested += (_, _) => ExitApplication();
        _tray.UpdatePaused(_clipService.IsPaused);

        _clipService.PausedChanged += (_, _) =>
        {
            _tray.UpdatePaused(_clipService.IsPaused);
            ApplyMonitorState();
        };

        ApplyMonitorState();
        RegisterHotKeys(showDialogOnFailure: false);

        StartPipeServer();

        if (!_settings.Current.FirstRunCompleted)
        {
            // 首次启动：先让用户决定热键与排除名单，再进入托盘常驻
            Log.Info("首次启动，打开初始设置");
            OpenSettings(firstRun: true);
        }
    }

    // ------------------------------------------------------------ 采集

    private void ApplyMonitorState()
    {
        if (_monitor is null || _settings is null || _clipService is null) return;

        var shouldListen = _settings.Current.EnableMonitor && !_clipService.IsPaused;

        if (shouldListen)
        {
            if (!_monitor.Start()) Log.Warn("剪贴板监听未能启动");
        }
        else
        {
            _monitor.Stop();
        }
    }

    private void OnTextCaptured(object? sender, TextCapturedEventArgs e)
    {
        // 我们自己写回剪贴板引起的事件，不能当成新内容
        if (ClipboardWriter.IsEchoOfOwnWrite(e.Text)) return;

        try
        {
            _clipService?.Capture(e.Text, e.Source.ProcessName, e.Source.Title);
        }
        catch (Exception ex)
        {
            Log.Error("写入片段失败", ex);
        }
    }

    // ------------------------------------------------------------ 热键

    private void RegisterHotKeys(bool showDialogOnFailure)
    {
        if (_hotKeys is null || _settings is null) return;

        _hotKeys.UnregisterAll();

        var hotKeys = _settings.Current.HotKeys;
        var failures = new List<string>(2);

        if (hotKeys.ToggleWindowEnabled
            && !_hotKeys.TryRegister(HotKeyToggleWindow, hotKeys.ToggleWindow, ShowMainWindow, out var toggleError)
            && toggleError is not null)
        {
            failures.Add(toggleError);
        }

        if (hotKeys.ManualCaptureEnabled
            && !_hotKeys.TryRegister(HotKeyManualCapture, hotKeys.ManualCapture, CaptureCurrentClipboard, out var captureError)
            && captureError is not null)
        {
            failures.Add(captureError);
        }

        if (_viewModel is not null)
        {
            _viewModel.Notice = failures.Count == 0 ? null : string.Join("；", failures);
        }

        if (failures.Count == 0 || !showDialogOnFailure) return;

        MessageBox.Show(
            string.Join("\n", failures) + "\n\n可以到「设置 → 快捷键」里换一个组合键。",
            "快捷键注册失败",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void CaptureCurrentClipboard()
    {
        string? text = null;
        try
        {
            if (System.Windows.Clipboard.ContainsText()) text = System.Windows.Clipboard.GetText();
        }
        catch (Exception ex)
        {
            Log.Warn($"读取剪贴板失败：{ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _tray?.ShowError("剪贴板里没有可收藏的文本");
            return;
        }

        var source = _foregroundTracker?.Current ?? ForegroundSnapshot.Empty;
        var saved = _clipService?.Capture(text, source.ProcessName, source.Title, manual: true);

        if (saved is null)
        {
            _tray?.ShowError("没有收藏成功，详情见日志");
            return;
        }

        var preview = saved.Preview(40);
        _tray?.ShowInfo($"已收藏：{preview}");
    }

    // ------------------------------------------------------------ 窗口

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;

        // 窗口已经在最前面时，再按一次快捷键就是收起（符合这类工具的手感）
        if (_mainWindow.IsVisible && _mainWindow.IsActive)
        {
            _mainWindow.HideWindow();
            return;
        }

        _mainWindow.ShowNearCursor();
    }

    private void OpenSettings(bool firstRun)
    {
        if (_settings is null || _clipService is null || _mainWindow is null) return;

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        // 设置窗口是模态的，期间主窗口不能因为失焦而自动隐藏
        using (_mainWindow.SuppressAutoHide())
        {
            var window = new SettingsWindow(_settings, _clipService, firstRun);
            if (_mainWindow.IsVisible) window.Owner = _mainWindow;

            _settingsWindow = window;
            window.Saved += OnSettingsSaved;
            window.DataCleared += (_, _) => _viewModel?.Reload();
            window.Closed += (_, _) =>
            {
                window.Saved -= OnSettingsSaved;
                _settingsWindow = null;
            };

            window.ShowDialog();
        }
    }

    private void OnSettingsSaved(object? sender, EventArgs e)
    {
        ApplyMonitorState();
        RegisterHotKeys(showDialogOnFailure: true);
        _viewModel?.Reload();
    }

    /// <summary>托盘菜单里的「清空未收藏的片段」。会二次确认，不可撤销。</summary>
    private void ClearFromTray()
    {
        if (_repository is null || _clipService is null) return;

        var total = _repository.Count();
        if (total == 0)
        {
            _tray?.ShowInfo("现在没有任何片段");
            return;
        }

        var answer = MessageBox.Show(
            $"将删除所有「未置顶且未收藏」的片段（共 {total} 条里的大部分）。\n\n此操作不可撤销，确定继续吗？",
            "清空未收藏的片段",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK) return;

        var removed = _repository.Clear(keepProtected: true);
        Log.Info($"用户从托盘清空了 {removed} 条片段");
        _viewModel?.Reload();
        _tray?.ShowInfo($"已删除 {removed} 条片段");
    }

    // ------------------------------------------------------------ 单实例

    private void StartPipeServer()
    {
        _pipeCancellation = new CancellationTokenSource();
        var token = _pipeCancellation.Token;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                    using var reader = new StreamReader(server);
                    var command = await reader.ReadLineAsync(token).ConfigureAwait(false);

                    if (string.Equals(command, ShowCommand, StringComparison.Ordinal))
                    {
                        await Dispatcher.InvokeAsync(ShowMainWindow);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Error("单实例管道异常", ex);

                    try
                    {
                        await Task.Delay(500, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }, token);
    }

    /// <summary>第二个实例被启动时，通知已经在跑的实例把窗口亮出来。</summary>
    private static bool TrySignalRunningInstance()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(800);

                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(ShowCommand);
                return true;
            }
            catch (TimeoutException)
            {
                // 第一个实例可能还没建好管道，稍等再来
                Thread.Sleep(200);
            }
            catch (Exception)
            {
                Thread.Sleep(200);
            }
        }

        return false;
    }

    // ------------------------------------------------------------ 生命周期

    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("界面线程未处理异常", args.Exception);
            args.Handled = true;

            MessageBox.Show(
                $"出了点问题：{args.Exception.Message}\n\n程序会继续运行，详情见日志。",
                "ClipNest",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("后台线程未处理异常", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("未观察的任务异常", args.Exception);
            args.SetObserved();
        };
    }

    private void ExitApplication()
    {
        if (_exiting) return;

        _exiting = true;
        Log.Info("==== ClipNest 退出 ====");

        Cleanup();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    private void Cleanup()
    {
        try
        {
            _pipeCancellation?.Cancel();
            _pipeCancellation?.Dispose();
            _pipeCancellation = null;
        }
        catch (Exception)
        {
            // 忽略
        }

        _mainWindow?.PrepareForExit();
        _monitor?.Dispose();
        _hotKeys?.Dispose();
        _hiddenWindow?.Dispose();
        _foregroundTracker?.Dispose();
        _tray?.Dispose();

        _monitor = null;
        _hotKeys = null;
        _hiddenWindow = null;
        _foregroundTracker = null;
        _tray = null;

        // 不清池会留下文件句柄，数据库文件可能删不掉
        SqliteConnection.ClearAllPools();

        if (_mutex is null) return;

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // 不持有该互斥体时释放会抛这个，忽略
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
