using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Pasteup.App.Services;
using Xunit;

namespace Pasteup.Tests;

/// <summary>
/// 设置页分段选择钮（「主题」三钮 / 「面板锚点」两钮）的主题响应回归（2026-11-14）。
/// 背景：选中态原先把 FindResource 取到的画刷**实例**写进 Background/Foreground 局部值。
/// 主题切换 = 整体替换 App.Resources 合并字典位置 0（ThemeManager.ApplyRaw），旧字典里的画刷实例不会变色，
/// 而设置窗隐藏不销毁（Loaded 只跑一次），于是切到浅色后两个锚点钮仍停在深色令牌上——
/// 用户看到的就是「浅色主题下这两颗按钮全黑，很突兀」。
/// 修复后 <see cref="SegmentButtons.SetActive"/> 只挂资源引用（代码版 DynamicResource），换字典即重解析。
/// 本用例直接验证这条不变量：换字典 → 颜色必须跟着变，且不需要重跑任何同步代码。
/// </summary>
public class SegmentButtonTests
{
    // 令牌实测值（同 ThemeTokenTests 锁定的 AccentTintBrush / ControlBgBrush / AccentBrush / TextBrush）
    private static readonly Color LightActive = Color.FromRgb(0xE3, 0xEE, 0xF0);
    private static readonly Color LightIdle = Color.FromRgb(0xFF, 0xFF, 0xFF);
    private static readonly Color DarkActive = Color.FromRgb(0x1E, 0x2B, 0x3D);
    private static readonly Color DarkIdle = Color.FromRgb(0x21, 0x20, 0x23);

    [Fact]
    public void Selected_segment_recolors_when_theme_dictionary_is_swapped()
    {
        RunSta(() =>
        {
            var repo = FindRepo();
            var host = HostWith(repo, out var btn);
            SegmentButtons.SetActive(btn, active: true);

            Assert.Equal(LightActive, BackgroundOf(btn));
            Assert.Equal(Color.FromRgb(0x1B, 0x6C, 0x80), ForegroundOf(btn)); // AccentBrush 浅

            // 运行时换主题 = 只替换合并字典位置 0，不重跑 SyncThemeButtons/SyncAnchorButtons
            host.Resources.MergedDictionaries[0] = ThemeDict(repo, dark: true);

            Assert.Equal(DarkActive, BackgroundOf(btn));
            Assert.Equal(Color.FromRgb(0x4A, 0x8A, 0xD6), ForegroundOf(btn)); // AccentBrush 深
        });
    }

    [Fact]
    public void Idle_segment_recolors_when_theme_dictionary_is_swapped()
    {
        RunSta(() =>
        {
            var repo = FindRepo();
            var host = HostWith(repo, out var btn);
            SegmentButtons.SetActive(btn, active: false);

            Assert.Equal(LightIdle, BackgroundOf(btn));
            Assert.Equal(Color.FromRgb(0x13, 0x14, 0x16), ForegroundOf(btn)); // TextBrush 浅

            host.Resources.MergedDictionaries[0] = ThemeDict(repo, dark: true);

            Assert.Equal(DarkIdle, BackgroundOf(btn));
            Assert.Equal(Colors.White, ForegroundOf(btn)); // TextBrush 深
        });
    }

    [Fact]
    public void Picking_another_option_moves_the_selection_in_place()
    {
        RunSta(() =>
        {
            var repo = FindRepo();
            var host = HostWith(repo, out var caret);
            var cursor = new Button();
            ((StackPanel)host.Child).Children.Add(cursor);

            SegmentButtons.SetActive(caret, active: true);
            SegmentButtons.SetActive(cursor, active: false);
            Assert.Equal(LightActive, BackgroundOf(caret));
            Assert.Equal(LightIdle, BackgroundOf(cursor));

            SegmentButtons.SetActive(caret, active: false);
            SegmentButtons.SetActive(cursor, active: true);
            Assert.Equal(LightIdle, BackgroundOf(caret));
            Assert.Equal(LightActive, BackgroundOf(cursor));

            // 换主题后两个钮一起跟着走（旧实现里这一步只有被点击过的那个才能靠 Sync 修正）
            host.Resources.MergedDictionaries[0] = ThemeDict(repo, dark: true);
            Assert.Equal(DarkIdle, BackgroundOf(caret));
            Assert.Equal(DarkActive, BackgroundOf(cursor));
        });
    }

    /// <summary>按钮必须挂在有资源的作用域下，资源引用才解析得到（游离元素只能看到 Application.Resources）。</summary>
    private static Border HostWith(string repo, out Button button)
    {
        button = new Button();
        var stack = new StackPanel();
        stack.Children.Add(button);
        var host = new Border { Child = stack };
        host.Resources.MergedDictionaries.Add(ThemeDict(repo, dark: false));
        return host;
    }

    private static Color BackgroundOf(Button b) => Assert.IsType<SolidColorBrush>(b.Background).Color;

    private static Color ForegroundOf(Button b) => Assert.IsType<SolidColorBrush>(b.Foreground).Color;

    private static ResourceDictionary LoadDict(string path)
    {
        using var stream = File.OpenRead(path);
        return Assert.IsType<ResourceDictionary>(XamlReader.Load(stream));
    }

    /// <summary>等价于 Theme.Light/Dark.xaml（Base + 明/暗令牌），按绝对路径松散载入以避免相对 Source 解析。</summary>
    private static ResourceDictionary ThemeDict(string repo, bool dark)
    {
        var dir = Path.Combine(repo, "src", "Pasteup.Themes", "Themes");
        var dict = new ResourceDictionary();
        dict.MergedDictionaries.Add(LoadDict(Path.Combine(dir, "Tokens.Base.xaml")));
        dict.MergedDictionaries.Add(LoadDict(Path.Combine(dir, dark ? "Tokens.Dark.xaml" : "Tokens.Light.xaml")));
        return dict;
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new InvalidOperationException("STA 线程失败", error);
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Pasteup.Themes")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
