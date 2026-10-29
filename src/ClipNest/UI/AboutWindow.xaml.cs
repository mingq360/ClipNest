using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using ClipNest.Infrastructure.Data;
using ClipNest.Infrastructure.Diagnostics;

namespace ClipNest.UI;

public partial class AboutWindow : Window
{
    private const string RepositoryUrl = "https://github.com/mingq360/ClipNest";

    public AboutWindow()
    {
        InitializeComponent();

        VersionText.Text = $"版本 {ReadVersion()}";
        DataPathText.Text = Db.DataDirectory;

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
    }

    private static string ReadVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        if (version is null) return "0.1.0";

        // 0.1.0.0 → 0.1.0
        return version.Revision > 0
            ? version.ToString()
            : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    private void OnOpenSourceClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = RepositoryUrl,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("打开 GitHub 页面失败", ex);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

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
}
