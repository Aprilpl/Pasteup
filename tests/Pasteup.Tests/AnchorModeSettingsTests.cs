using System.IO;
using System.Text.Json;
using Pasteup.Core.Settings;
using Xunit;

namespace Pasteup.Tests;

/// <summary>面板锚点来源设置（2026-11-10，阶段 3）：默认跟随光标、坏值回退默认、持久化往返。</summary>
public class AnchorModeSettingsTests
{
    [Fact]
    public void Default_anchor_mode_is_caret()
    {
        Assert.Equal(PanelAnchorMode.Caret, new AppSettings().AnchorMode);
    }

    [Fact]
    public void Anchor_mode_round_trips_through_settings_store()
    {
        var dir = TempDir();
        try
        {
            var store = new SettingsStore(dir);
            store.Save(new AppSettings { AnchorMode = PanelAnchorMode.Cursor });
            Assert.Equal(PanelAnchorMode.Cursor, store.Load().AnchorMode);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Undefined_anchor_mode_falls_back_to_defaults()
    {
        var dir = TempDir();
        try
        {
            // STJ 数字枚举不做范围校验：99 会被读成非法枚举值，IsValid 必须挡住并整体回退默认
            File.WriteAllText(Path.Combine(dir, "settings.json"), "{\"AnchorMode\":99}");
            var loaded = new SettingsStore(dir).Load();
            Assert.Equal(PanelAnchorMode.Caret, loaded.AnchorMode);
            Assert.False(new AppSettings { AnchorMode = (PanelAnchorMode)99 }.IsValid());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Missing_anchor_mode_in_old_settings_file_defaults_to_caret()
    {
        var dir = TempDir();
        try
        {
            // 旧版本 settings.json 没有 AnchorMode 字段：反序列化后走属性默认值
            File.WriteAllText(Path.Combine(dir, "settings.json"),
                JsonSerializer.Serialize(new { InvokeHotkey = "Alt+V", ScreenshotHotkey = "Alt+S" }));
            Assert.Equal(PanelAnchorMode.Caret, new SettingsStore(dir).Load().AnchorMode);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pasteup-anchor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
