using ClipNest.Infrastructure.Diagnostics;
using Microsoft.Win32;

namespace ClipNest.Infrastructure.Startup;

/// <summary>开机自启：写 HKCU 的 Run 键，不需要管理员权限。</summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClipNest";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Error("读取开机自启状态失败", ex);
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                var exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    Log.Warn("无法确定程序路径，跳过开机自启设置");
                    return false;
                }

                key.SetValue(ValueName, $"\"{exePath}\" --tray");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("设置开机自启失败", ex);
            return false;
        }
    }
}
