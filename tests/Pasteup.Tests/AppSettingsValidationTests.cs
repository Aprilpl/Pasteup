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

    [Fact]
    public void Load_with_legacy_invalid_hotkey_resets_only_hotkeys_not_other_settings()
    {
        var dir = TempDir();
        try
        {
            // 2026-11-13 修复前的遗留形态：按 Ctrl 的 KeyDown 被录成「Ctrl+VKA2」并落盘。
            // 只回退热键字段，主题/容量等其他设置必须保留（旧行为是 IsValid 失败 → 整档回默认）。
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                "{\"Theme\":2,\"InvokeHotkey\":\"Ctrl+VKA2\",\"ScreenshotHotkey\":\"Ctrl+VKA3\",\"HistoryCapacity\":321}");
            var loaded = new SettingsStore(dir).Load();

            Assert.Equal(AppSettings.DefaultInvokeHotkey, loaded.InvokeHotkey);
            Assert.Equal(AppSettings.DefaultScreenshotHotkey, loaded.ScreenshotHotkey);
            Assert.Equal(ThemeMode.Dark, loaded.Theme);
            Assert.Equal(321, loaded.HistoryCapacity);
            Assert.True(loaded.IsValid());
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void SanitizeHotkeys_keeps_valid_custom_hotkeys()
    {
        var s = new AppSettings { InvokeHotkey = "Ctrl+J", ScreenshotHotkey = "Alt+J" };

        s.SanitizeHotkeys();

        Assert.Equal("Ctrl+J", s.InvokeHotkey);
        Assert.Equal("Alt+J", s.ScreenshotHotkey);
    }

    [Fact]
    public void Default_invoke_hotkey_stays_alt_v_and_start_with_windows_defaults_off()
    {
        // 2026-11-14：需求明确「唤醒面板默认仍为 Alt+V」——顺带锁定截图热键默认值与新设置默认关，
        // 防止后续改动误改默认值（既有用户升级后行为漂移）。
        var s = new AppSettings();
        Assert.Equal("Alt+V", s.InvokeHotkey);
        Assert.Equal(AppSettings.DefaultInvokeHotkey, s.InvokeHotkey);
        Assert.Equal("Alt+S", s.ScreenshotHotkey);
        Assert.False(s.StartWithWindows);
        Assert.True(s.IsValid());
    }

    [Fact]
    public void SettingsStore_roundtrips_StartWithWindows()
    {
        var dir = TempDir();
        try
        {
            new SettingsStore(dir).Save(new AppSettings { StartWithWindows = true });
            // 重新构造 store 再读：确认真的经过 JSON 落盘/读回（设置页开关可持久化）
            Assert.True(new SettingsStore(dir).Load().StartWithWindows);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    [Fact]
    public void Legacy_settings_without_StartWithWindows_field_defaults_to_off()
    {
        var dir = TempDir();
        try
        {
            // 升级路径：旧版 settings.json 没有该字段；bool 缺省即 false，无需迁移逻辑
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"Theme\":2}");
            Assert.False(new SettingsStore(dir).Load().StartWithWindows);
        }
        finally
        {
            Cleanup(dir);
        }
    }

    private static string TempDir()    {
        var dir = Path.Combine(Path.GetTempPath(), "pasteup-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
