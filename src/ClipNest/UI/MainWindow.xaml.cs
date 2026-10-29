using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ClipNest.Infrastructure.Diagnostics;
using ClipNest.Infrastructure.Native;
using ClipNest.Services;
using ClipNest.UI.ViewModels;

namespace ClipNest.UI;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly SettingsService _settings;
    private bool _suppressAutoHide;
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel, SettingsService settings)
    {
        InitializeComponent();

        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;

        _viewModel.HideRequested += (_, _) => HideWindow();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        PreviewKeyDown += OnWindowPreviewKeyDown;
        Deactivated += OnDeactivated;
        Closing += OnClosing;
    }

    /// <summary>用户从标题栏打开设置。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>真正退出时先调用，否则关闭只会被拦成隐藏。</summary>
    public void PrepareForExit()
    {
        _allowClose = true;
        _viewModel.Dispose();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;

        // 常驻工具：点关闭按钮只是收起，不是退出
        e.Cancel = true;
        HideWindow();
    }

    /// <summary>显示在鼠标所在显示器的正中，并聚焦搜索框。</summary>
    public void ShowNearCursor()
    {
        if (!IsVisible)
        {
            // 先以透明状态显示，才能拿到当前显示器的 DPI 再定位，避免在副屏上跳一下
            Opacity = 0;
            Show();
        }

        CenterOnCursorScreen();
        Opacity = 1;

        Activate();
        FocusSearch();
        _viewModel.RefreshStatusOnly();
    }

    public void HideWindow()
    {
        if (!IsVisible) return;

        Hide();

        // 收起时清空搜索词：下次唤起默认是全局视图，符合「找最近的」这个主场景
        if (!string.IsNullOrEmpty(_viewModel.SearchText)) _viewModel.SearchText = string.Empty;
    }

    /// <summary>打开模态子窗口前调用，避免主窗口因为失焦被自动隐藏。</summary>
    public IDisposable SuppressAutoHide()
    {
        _suppressAutoHide = true;
        return new AutoHideScope(this);
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    /// <summary>在鼠标所在显示器的工作区里居中，而不是贴在屏幕上沿。</summary>
    private void CenterOnCursorScreen()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            var (monitor, dpiX, dpiY) = NativeMethods.GetCursorMonitorDpi();
            var workArea = NativeMethods.GetMonitorWorkArea(monitor);
            if (workArea.Width <= 0 || workArea.Height <= 0) return;

            var width = ActualWidth > 0 ? ActualWidth : Width;
            var height = ActualHeight > 0 ? ActualHeight : Height;
            if (width <= 0 || height <= 0) return;

            // 全程物理像素，而不是设 Left/Top：
            // Left/Top 是 DIP，WPF 会按「窗口当前所在屏幕」的缩放比去换算，
            // 此时窗口还挂在主屏上，副屏缩放比不同就会整体偏掉（多屏最常见的坑）。
            // 另外窗口挪到另一个缩放比的屏幕后尺寸本身也会变，这里按目标屏的缩放比折算。
            var physicalWidth = (int)Math.Round(width * (dpiX / 96.0));
            var physicalHeight = (int)Math.Round(height * (dpiY / 96.0));

            var x = workArea.Left + (workArea.Width - physicalWidth) / 2;
            var y = workArea.Top + (workArea.Height - physicalHeight) / 2;

            x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - physicalWidth));
            y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - physicalHeight));

            NativeMethods.MoveWindowPixels(handle, x, y);
        }
        catch (Exception ex)
        {
            Log.Error("窗口定位失败", ex);
        }
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_suppressAutoHide) return;
        if (!_settings.Current.HideOnLostFocus) return;

        HideWindow();
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
            // 鼠标已抬起，忽略
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => HideWindow();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OnAboutClick(object sender, RoutedEventArgs e)
    {
        // 弹关于窗口时主窗口会失焦，得先屏蔽自动隐藏，否则窗口在弹窗后面消失
        using (SuppressAutoHide())
        {
            var about = new AboutWindow { Owner = this };
            about.ShowDialog();
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.SelectedRow is null) return;
        _viewModel.CopySelected();
    }

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                _viewModel.CopySelected();
                e.Handled = true;
                break;
            case Key.Escape:
                HideWindow();
                e.Handled = true;
                break;
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 正文可编辑：编辑框内时把按键交给文本框（删除字符、撤销/重做等正常编辑），
        // 仅 Ctrl+S 特意映射为「保存修改」，与图标按钮保持一致。
        var contentFocused = ContentBox.IsKeyboardFocusWithin;

        if (e.Key == Key.Escape)
        {
            HideWindow();
            e.Handled = true;
            return;
        }

        var searchFocused = SearchBox.IsKeyboardFocusWithin;

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.S && contentFocused)
            {
                if (_viewModel.SaveCommand.CanExecute(null)) _viewModel.SaveCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.Key == Key.P && !contentFocused)
            {
                _viewModel.TogglePin();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.S && !contentFocused)
            {
                _viewModel.ToggleStar();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F)
            {
                FocusSearch();
                e.Handled = true;
                return;
            }
        }

        // 编辑框内的 Delete / 斜杠等留给文本框正常输入，不当作删除条目的快捷键
        if (e.Key == Key.Delete && !searchFocused && !contentFocused)
        {
            _viewModel.DeleteSelected();
            e.Handled = true;
            return;
        }

        // 只有搜索框里没内容、且不在编辑正文时，/ 才当作「回到搜索框」的快捷键
        if (e.Key == Key.OemQuestion && !searchFocused && !contentFocused && string.IsNullOrEmpty(_viewModel.SearchText))
        {
            FocusSearch();
            e.Handled = true;
        }
    }

    /// <summary>切换选中片段后清空编辑框的撤销/重做历史，避免对上一片段的撤销误伤当前内容。</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedRow)) return;

        // 延迟到 EditContent 的绑定把新内容灌进文本框之后再重置撤销栈
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ContentBox.IsUndoEnabled = false;
            ContentBox.IsUndoEnabled = true;
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void MoveSelection(int delta)
    {
        var count = _viewModel.Items.Count;
        if (count == 0) return;

        var index = _viewModel.SelectedRow is null ? -1 : _viewModel.Items.IndexOf(_viewModel.SelectedRow);
        var next = Math.Clamp(index + delta, 0, count - 1);

        _viewModel.SelectedRow = _viewModel.Items[next];
        ClipList.ScrollIntoView(_viewModel.SelectedRow);
    }

    private sealed class AutoHideScope : IDisposable
    {
        private readonly MainWindow _window;

        public AutoHideScope(MainWindow window) => _window = window;

        public void Dispose() => _window._suppressAutoHide = false;
    }
}
