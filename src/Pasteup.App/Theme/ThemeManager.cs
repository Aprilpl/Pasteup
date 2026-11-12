using System.Windows;
using Pasteup.Core.Settings;
using ThemeMode = Pasteup.Core.Settings.ThemeMode;

namespace Pasteup.App.Theme;

/// <summary>运行时主题切换：整体替换 App.Resources 合并字典位置 0（Theme.Light/Dark）。</summary>
public static class ThemeManager
{
    private const string LightUri = "pack://application:,,,/Pasteup.Themes;component/Themes/Theme.Light.xaml";
    private const string DarkUri = "pack://application:,,,/Pasteup.Themes;component/Themes/Theme.Dark.xaml";

    public static void Apply(ThemeMode mode)
    {
        var dark = mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => !SystemTheme.SystemPrefersLight(),
        };
        ApplyRaw(dark);
    }

    public static void ApplyRaw(bool dark)
    {
        if (Application.Current?.Resources is not { } resources) return;
        var target = new Uri(dark ? DarkUri : LightUri);
        var md = resources.MergedDictionaries;
        if (md.Count == 0)
        {
            md.Add(new ResourceDictionary { Source = target });
            return;
        }
        if (md[0].Source == target) return;
        md[0] = new ResourceDictionary { Source = target };
    }
}
