using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClipNest.Services;

namespace ClipNest.UI.Controls;

/// <summary>
/// 快捷键录制框：获得焦点后直接按下组合键即可。
/// 不接受「无修饰键」的组合，否则会抢占正常打字。
/// </summary>
public partial class HotKeyBox : UserControl
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture),
        typeof(string),
        typeof(HotKeyBox),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnGestureChanged));

    public HotKeyBox()
    {
        InitializeComponent();
        UpdateVisual();
    }

    /// <summary>录制到新组合键时触发（程序化赋值也会触发，调用方按需忽略）。</summary>
    public event EventHandler? GestureChanged;

    /// <summary>形如 "Ctrl+Alt+V"。空字符串表示未设置。</summary>
    public string Gesture
    {
        get => (string)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    private static void OnGestureChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (HotKeyBox)d;
        box.UpdateVisual();
        box.GestureChanged?.Invoke(box, EventArgs.Empty);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Root.Focus();
        Keyboard.Focus(Root);
        e.Handled = true;
    }

    private void OnFocusChanged(object sender, KeyboardFocusChangedEventArgs e) => UpdateVisual();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 单独按修饰键：等主键，不当作结果
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            ShowHint("继续按主键…");
            e.Handled = true;
            return;
        }

        // 没有修饰键时放行 Tab，否则用户无法用键盘离开这个控件
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None) return;

        e.Handled = true;

        if (key == Key.Escape)
        {
            Gesture = string.Empty;
            UpdateVisual();
            return;
        }

        var modifiers = ToModifiers(Keyboard.Modifiers);
        if (modifiers == 0)
        {
            ShowHint("需要配合 Ctrl / Alt / Shift / Win");
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            ShowHint("这个按键不能用作快捷键");
            return;
        }

        Gesture = HotKeyGesture.Format(modifiers, virtualKey);
        UpdateVisual();
    }

    private static int ToModifiers(ModifierKeys modifiers)
    {
        var result = 0;
        if (modifiers.HasFlag(ModifierKeys.Control)) result |= HotKeyGesture.ModControl;
        if (modifiers.HasFlag(ModifierKeys.Alt)) result |= HotKeyGesture.ModAlt;
        if (modifiers.HasFlag(ModifierKeys.Shift)) result |= HotKeyGesture.ModShift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) result |= HotKeyGesture.ModWin;
        return result;
    }

    private void UpdateVisual()
    {
        var focused = Root.IsKeyboardFocusWithin;
        var hasGesture = !string.IsNullOrWhiteSpace(Gesture);

        // 用 SetResourceReference 而不是直接赋画刷：切换深浅色时能跟着重新解析
        Root.SetResourceReference(Border.BorderBrushProperty, focused ? "Accent" : "BorderSoft");

        if (hasGesture)
        {
            ValueText.Text = Gesture;
            ValueText.Visibility = Visibility.Visible;
            HintText.Visibility = Visibility.Collapsed;
            return;
        }

        ValueText.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Visible;
        HintText.Text = focused ? "按下组合键…" : "点击后录制";
    }

    private void ShowHint(string message)
    {
        ValueText.Visibility = Visibility.Collapsed;
        HintText.Visibility = Visibility.Visible;
        HintText.Text = message;
    }
}
