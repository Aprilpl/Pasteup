using Microsoft.Win32;

namespace Pasteup.Core.Settings;

/// <summary>读取 Windows 系统深浅色偏好（Personalize\AppsUseLightTheme）。</summary>
public static class SystemTheme
{
    public static bool SystemPrefersLight()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { /* 注册表不可读时按浅色处理 */ }
        return true;
    }
}
