using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Xunit;

namespace Pasteup.Tests;

/// <summary>
/// 令牌层完整性：Tokens.Light / Tokens.Dark / Tokens.Base 逐键校验（键齐全、两主题键集一致、
/// 品牌色与原型一致、透明度换算正确）。XAML 在 STA 线程加载并就地抽取为 CLR 值（规避 Freezable 线程亲和）。
/// </summary>
public class ThemeTokenTests
{
    private static T RunSta<T>(Func<T> f)
    {
        T? result = default;
        var t = new Thread(() => result = f());
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        return result!;
    }

    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Pasteup.Themes")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    /// <summary>加载 XAML 并在 STA 线程内抽取全部键值（画刷→Color、圆角/字号→数值、字体→Source 字符串）。</summary>
    private static Dictionary<string, object?> LoadValues(string file)
    {
        var path = Path.Combine(FindRepo(), "src", "Pasteup.Themes", "Themes", file);
        return RunSta(() =>
        {
            var dict = (ResourceDictionary)XamlReader.Load(File.OpenRead(path));
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in dict.Keys.Cast<string>())
            {
                result[key] = dict[key] switch
                {
                    SolidColorBrush b => b.Color,
                    Color c => c,
                    CornerRadius cr => cr,
                    FontFamily f => f.Source,
                    var other => other,
                };
            }
            return result;
        });
    }

    [Theory]
    [InlineData("Tokens.Light.xaml", "FrameBrush", "#EAF0F1")]
    [InlineData("Tokens.Light.xaml", "SheetBrush", "#FFFFFF")]
    [InlineData("Tokens.Light.xaml", "PanelBrush", "#F4F6F8")]
    [InlineData("Tokens.Light.xaml", "RowHoverBrush", "#F2F5F6")]
    [InlineData("Tokens.Light.xaml", "EdgeBrush", "#E8EAEC")]
    [InlineData("Tokens.Light.xaml", "RuleBrush", "#F0F1F2")]
    [InlineData("Tokens.Light.xaml", "PopupBorderBrush", "#DFE5E6")]
    [InlineData("Tokens.Light.xaml", "WinHoverBrush", "#DDE5E7")]
    [InlineData("Tokens.Light.xaml", "TextBrush", "#131416")]
    [InlineData("Tokens.Light.xaml", "MutedBrush", "#5D5F60")]
    [InlineData("Tokens.Light.xaml", "AccentBrush", "#1B6C80")]
    [InlineData("Tokens.Light.xaml", "AccentTintBrush", "#E3EEF0")]
    [InlineData("Tokens.Light.xaml", "DangerBrush", "#B3352C")]
    [InlineData("Tokens.Light.xaml", "DangerStrongBrush", "#9B2D25")]
    [InlineData("Tokens.Light.xaml", "DangerInkBrush", "#FFFFFF")]
    [InlineData("Tokens.Light.xaml", "ModalScrimBrush", "#570A0C0E")]
    [InlineData("Tokens.Light.xaml", "ControlBgBrush", "#FFFFFF")]
    [InlineData("Tokens.Light.xaml", "ControlBorderBrush", "#CDCED1")]
    [InlineData("Tokens.Light.xaml", "KbdBgBrush", "#F3F4F5")]
    [InlineData("Tokens.Light.xaml", "KbdBorderBrush", "#E0E2E3")]
    [InlineData("Tokens.Light.xaml", "KbdTextBrush", "#3A3C3D")]
    [InlineData("Tokens.Light.xaml", "ChipBgBrush", "#F0FFFFFF")]
    [InlineData("Tokens.Light.xaml", "ChipTextBrush", "#3A3C3D")]
    [InlineData("Tokens.Light.xaml", "ChipBorderBrush", "#E8EAEC")]
    [InlineData("Tokens.Light.xaml", "SwitchOffTrackBrush", "#C9CCCD")]
    [InlineData("Tokens.Light.xaml", "SwitchOffKnobBrush", "#FFFFFF")]
    [InlineData("Tokens.Light.xaml", "SwitchOnTrackBrush", "#131416")]
    [InlineData("Tokens.Light.xaml", "SwitchOnKnobBrush", "#FFFFFF")]
    [InlineData("Tokens.Light.xaml", "ScrimBrush", "#8C000000")]
    [InlineData("Tokens.Dark.xaml", "FrameBrush", "#171719")]
    [InlineData("Tokens.Dark.xaml", "SheetBrush", "#131217")]
    [InlineData("Tokens.Dark.xaml", "PanelBrush", "#212023")]
    [InlineData("Tokens.Dark.xaml", "RowHoverBrush", "#1A191E")]
    [InlineData("Tokens.Dark.xaml", "EdgeBrush", "#2A2A2E")]
    [InlineData("Tokens.Dark.xaml", "RuleBrush", "#1B1A1F")]
    [InlineData("Tokens.Dark.xaml", "PopupBorderBrush", "#0E0E10")]
    [InlineData("Tokens.Dark.xaml", "WinHoverBrush", "#212023")]
    [InlineData("Tokens.Dark.xaml", "TextBrush", "#FFFFFF")]
    [InlineData("Tokens.Dark.xaml", "MutedBrush", "#9A9CA2")]
    [InlineData("Tokens.Dark.xaml", "AccentBrush", "#4A8AD6")]
    [InlineData("Tokens.Dark.xaml", "AccentTintBrush", "#1E2B3D")]
    [InlineData("Tokens.Dark.xaml", "DangerBrush", "#E5695E")]
    [InlineData("Tokens.Dark.xaml", "DangerStrongBrush", "#EE8177")]
    [InlineData("Tokens.Dark.xaml", "DangerInkBrush", "#131217")]
    [InlineData("Tokens.Dark.xaml", "ModalScrimBrush", "#570A0C0E")]
    [InlineData("Tokens.Dark.xaml", "ControlBgBrush", "#212023")]
    [InlineData("Tokens.Dark.xaml", "ControlBorderBrush", "#3A3A3E")]
    [InlineData("Tokens.Dark.xaml", "KbdBgBrush", "#212023")]
    [InlineData("Tokens.Dark.xaml", "KbdBorderBrush", "#2A2A2E")]
    [InlineData("Tokens.Dark.xaml", "KbdTextBrush", "#C6C7CB")]
    [InlineData("Tokens.Dark.xaml", "ChipBgBrush", "#D10E0E10")]
    [InlineData("Tokens.Dark.xaml", "ChipTextBrush", "#ECEDED")]
    [InlineData("Tokens.Dark.xaml", "ChipBorderBrush", "#00FFFFFF")]
    [InlineData("Tokens.Dark.xaml", "SwitchOffTrackBrush", "#3A3A3E")]
    [InlineData("Tokens.Dark.xaml", "SwitchOffKnobBrush", "#B9BABD")]
    [InlineData("Tokens.Dark.xaml", "SwitchOnTrackBrush", "#E9E9EB")]
    [InlineData("Tokens.Dark.xaml", "SwitchOnKnobBrush", "#171719")]
    [InlineData("Tokens.Dark.xaml", "ScrimBrush", "#8C000000")]
    public void Theme_colors_match_prototype(string file, string key, string expectedHex)
    {
        var color = Assert.IsType<Color>(LoadValues(file)[key]);
        Assert.Equal((Color)ColorConverter.ConvertFromString(expectedHex)!, color);
    }

    [Fact]
    public void Both_themes_expose_identical_key_sets()
    {
        var light = LoadValues("Tokens.Light.xaml").Keys.ToHashSet();
        var dark = LoadValues("Tokens.Dark.xaml").Keys.ToHashSet();
        Assert.True(light.SetEquals(dark),
            $"两主题键集不一致：仅浅色有 [{string.Join(",", light.Except(dark))}]，仅深色有 [{string.Join(",", dark.Except(light))}]");
    }

    [Theory]
    [InlineData("SheetRadius", 14)]
    [InlineData("CardRadius", 8)]
    [InlineData("KbdRadius", 4)]
    public void Base_corner_radii(string key, int expected)
        => Assert.Equal(new CornerRadius(expected), LoadValues("Tokens.Base.xaml")[key]);

    [Theory]
    [InlineData("FontSizeTitle", 15)]
    [InlineData("FontSizeLabel", 13.5)]
    [InlineData("FontSizeRow", 13)]
    [InlineData("FontSizeDesc", 12.5)]
    [InlineData("FontSizeNote", 12)]
    [InlineData("FontSizeMeta", 11.5)]
    [InlineData("FontSizeMicro", 11)]
    public void Base_type_scale(string key, double expected)
        => Assert.Equal(expected, LoadValues("Tokens.Base.xaml")[key]);

    [Fact]
    public void Base_fonts_defined()
    {
        var fonts = LoadValues("Tokens.Base.xaml");
        var sans = Assert.IsType<string>(fonts["FontSans"]);
        var mono = Assert.IsType<string>(fonts["FontMono"]);
        Assert.Contains("IBM Plex Sans", sans);
        Assert.Contains("Microsoft YaHei UI", sans);
        Assert.Contains("IBM Plex Mono", mono);
    }

    [Fact]
    public void On_accent_brush_is_white()
        => Assert.Equal(Colors.White, LoadValues("Tokens.Base.xaml")["OnAccentBrush"]);

    [Fact]
    public void Light_dark_are_actually_light_and_dark()
    {
        var light = (Color)LoadValues("Tokens.Light.xaml")["FrameBrush"]!;
        var dark = (Color)LoadValues("Tokens.Dark.xaml")["FrameBrush"]!;
        Assert.True(light.R > 200 && light.G > 200, "浅色 frame 应接近白");
        Assert.True(dark.R < 40 && dark.G < 40, "深色 frame 应接近黑");
    }
}
