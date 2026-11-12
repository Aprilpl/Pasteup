using System.IO;
using Pasteup.Core.Settings;
using Xunit;

namespace Pasteup.Tests;

/// <summary>settings.json 的枚举越界校验（2026-11-10 安全审计 C 轴备注 2）：STJ 数字枚举不做范围
/// 校验，越界 Theme（如 99）必须在 <see cref="AppSettings.IsValid"/> 挡住，SettingsStore.Load
/// 整体回退默认值，否则主题等枚举被任意值驱动。</summary>
public class AppSettingsValidationTests
{
    [Fact]
    public void Default_theme_is_system_and_settings_are_valid()
    {
        var s = new AppSettings();
        Assert.Equal(ThemeMode.System, s.Theme);
        Assert.True(s.IsValid());
    }

    [Theory]
    [InlineData(ThemeMode.System)]
    [InlineData(ThemeMode.Light)]
    [InlineData(ThemeMode.Dark)]
    public void Defined_theme_values_pass_validation(ThemeMode theme)
        => Assert.True(new AppSettings { Theme = theme }.IsValid());

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(99)]
    public void Undefined_theme_value_is_invalid(int rawTheme)
        => Assert.False(new AppSettings { Theme = (ThemeMode)rawTheme }.IsValid());

    [Fact]
    public void Load_with_out_of_range_theme_falls_back_to_defaults()
    {
        var dir = TempDir();
        try
        {
            // STJ 直接把 99 读成 (ThemeMode)99（不抛）；IsValid 挡住 → Load 返回全新默认设置
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Theme\":99}");
            var loaded = new SettingsStore(dir).Load();
            Assert.Equal(ThemeMode.System, loaded.Theme);
            Assert.True(loaded.IsValid());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Load_with_defined_theme_is_preserved()
    {
        var dir = TempDir();
        try
        {
            // 反向保护：新校验不得把合法主题值误判成非法（Save→Load 往返）
            new SettingsStore(dir).Save(new AppSettings { Theme = ThemeMode.Dark });
            var loaded = new SettingsStore(dir).Load();
            Assert.Equal(ThemeMode.Dark, loaded.Theme);
            Assert.True(loaded.IsValid());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pasteup-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
