using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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

    /// <summary>显示在主窗口旁（鼠标所在显示器），并聚焦搜索框。</summary>
    public void ShowNearCursor()
    {
        if (!IsVisible)
        {
            // 先以透明状态显示，才能拿到当前显示器的 DPI 再定位，避免在副屏上跳一下
            Opacity = 0;
            Show();
        }

        PositionOnCursorScreen();
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

    private void PositionOnCursorScreen()
    {
        try
        {
            var workArea = NativeMethods.GetCursorWorkArea();
            if (workArea.Width <= 0 || workArea.Height <= 0) return;

            var dpi = VisualTreeHelper.GetDpi(this);
            var workLeft = workArea.Left / dpi.DpiScaleX;
            var workTop = workArea.Top / dpi.DpiScaleY;
            var workWidth = workArea.Width / dpi.DpiScaleX;
            var workHeight = workArea.Height / dpi.DpiScaleY;

            var width = ActualWidth > 0 ? ActualWidth : Width;
            var height = ActualHeight > 0 ? ActualHeight : Height;

            // 水平居中，垂直放在上方 1/5 处：视线落点自然，也不会挡住刚复制的内容
            Left = workLeft + (workWidth - width) / 2;
            Top = workTop + Math.Max(24, (workHeight - height) / 4);

            Left = Math.Max(workLeft, Math.Min(Left, workLeft + workWidth - width));
            Top = Math.Max(workTop, Math.Min(Top, workTop + workHeight - height));
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
        if (e.Key == Key.Escape)
        {
            HideWindow();
            e.Handled = true;
            return;
        }

        var searchFocused = SearchBox.IsKeyboardFocusWithin;

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.P:
                    _viewModel.TogglePin();
                    e.Handled = true;
                    return;
                case Key.S:
                    _viewModel.ToggleStar();
                    e.Handled = true;
                    return;
                case Key.F:
                    FocusSearch();
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Delete && !searchFocused)
        {
            _viewModel.DeleteSelected();
            e.Handled = true;
            return;
        }

        // 只有搜索框里没内容时，/ 才当作「回到搜索框」的快捷键
        if (e.Key == Key.OemQuestion && !searchFocused && string.IsNullOrEmpty(_viewModel.SearchText))
        {
            FocusSearch();
            e.Handled = true;
        }
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
