using System.IO;
using Pasteup.Core.History;
using Pasteup.Core.Settings;
using Xunit;

namespace Pasteup.Tests;

public class HistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pasteup-tests", Guid.NewGuid().ToString("N"));

    private HistoryStore Store => new(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Save_load_roundtrip()
    {
        var store = Store;
        var engine = new HistoryEngine();
        engine.Add(ClipboardEntry.FromText("hello", "Notepad"));
        store.Save(engine);

        var loaded = new HistoryStore(_dir).Load();
        Assert.Single(loaded);
        Assert.Equal("hello", loaded[0].FullText);
        Assert.Equal("Notepad", loaded[0].SourceApp);
    }

    [Fact]
    public void Load_empty_when_no_file()
        => Assert.Empty(Store.Load());

    [Fact]
    public void Corrupt_json_backed_up_and_returns_empty()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "history.json"), "{ this is not json !!!");
        var loaded = Store.Load();
        Assert.Empty(loaded);
        Assert.True(File.Exists(Path.Combine(_dir, "history.json.corrupt")));
    }

    [Fact]
    public void Image_save_load_roundtrip()
    {
        var store = Store;
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 }; // 内容无所谓，非空字节即可
        var id = Guid.NewGuid();
        var name = store.SaveImagePng(id, png);
        Assert.Equal($"{id:N}.png", name);
        Assert.Equal(png, store.LoadImagePng(name));
    }

    [Fact]
    public void Load_missing_image_returns_null()
        => Assert.Null(Store.LoadImagePng("ghost.png"));

    // ---- 安全加固（2026-11-02）：磁盘 history.json 是信任边界，篡改/半写不得炸启动 ----

    private void WriteHistory(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "history.json"), json);
    }

    [Fact]
    public void Load_entries_null_returns_empty()
    {
        WriteHistory("""{"Version":1,"Entries":null}""");
        Assert.Empty(Store.Load());
        // 非异常路径：原文件保留，不迁 .corrupt
        Assert.True(File.Exists(Path.Combine(_dir, "history.json")));
    }

    [Fact]
    public void Load_locked_file_returns_empty_without_throw()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "history.json"), "{}");
        // 独占锁：ReadAllText 抛 IOException、.corrupt 迁移也会失败——都须被吞掉
        using (File.Open(Path.Combine(_dir, "history.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Empty(Store.Load());
        }
    }

    [Fact]
    public void Load_drops_entry_with_out_of_range_kind()
    {
        WriteHistory("""
            {"Version":1,"Entries":[
            {"Id":"11111111-1111-1111-1111-111111111111","Kind":0,"Preview":"正常"},
            {"Id":"22222222-2222-2222-2222-222222222222","Kind":99}]}
            """);
        var loaded = Store.Load();
        var entry = Assert.Single(loaded);
        Assert.Equal("正常", entry.Preview);
    }

    [Theory]
    [InlineData(@"..\..\evil.png")]   // 相对穿越
    [InlineData(@"C:\evil.png")]      // 绝对路径
    [InlineData("evil.png")]          // 非 Guid 名
    [InlineData("ABCDEF01-2345-6789-ABCD-EF0123456789.png")] // 大写不认：SaveImagePng 恒小写
    public void Load_drops_image_entry_with_unsafe_file_name(string imageFile)
    {
        WriteHistory($$"""
            {"Version":1,"Entries":[
            {"Id":"33333333-3333-3333-3333-333333333333","Kind":1,"ImageFile":"{{imageFile}}"}]}
            """);
        Assert.Empty(Store.Load());
    }

    [Fact]
    public void Load_keeps_image_entry_with_valid_guid_name()
    {
        WriteHistory("""
            {"Version":1,"Entries":[
            {"Id":"44444444-4444-4444-4444-444444444444","Kind":1,
             "ImageFile":"0123456789abcdef0123456789abcdef.png"}]}
            """);
        Assert.Single(Store.Load());
    }

    [Fact]
    public void Load_repairs_null_preview_and_files()
    {
        WriteHistory("""
            {"Version":1,"Entries":[
            {"Id":"55555555-5555-5555-5555-555555555555","Kind":0,"Preview":null,"Files":null}]}
            """);
        var entry = Assert.Single(Store.Load());
        Assert.Equal(string.Empty, entry.Preview);
        Assert.Empty(entry.Files);
        // 修复后条目必须能穿过引擎搜索（"Files": null 曾让搜索每击键 NRE）
        var engine = new HistoryEngine();
        engine.Load([entry]);
        Assert.Empty(engine.Query(search: "任意词"));
    }

    [Theory]
    [InlineData(@"..\..\secret.png")]
    [InlineData(@"C:\evil.png")]
    [InlineData("UPPERCASE0123456789ABCDEF01234.png")]
    public void LoadImagePng_rejects_unsafe_file_names(string name)
        => Assert.Null(Store.LoadImagePng(name));

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef.png", true)]
    [InlineData("0123456789abcdef0123456789abcde.png", false)]  // 35 位
    [InlineData("0123456789abcdef0123456789abcdeg.png", false)] // 非十六进制 g
    [InlineData("0123456789ABCDEF0123456789ABCDEF.png", false)] // 大写
    public void IsValidImageFileName_shape_rules(string name, bool expected)
        => Assert.Equal(expected, HistoryStore.IsValidImageFileName(name));


    [Fact]
    public void Save_is_atomic_no_tmp_leftover()
    {
        var store = Store;
        var engine = new HistoryEngine();
        engine.Add(ClipboardEntry.FromText("x", null));
        store.Save(engine);
        Assert.False(File.Exists(Path.Combine(_dir, "history.json.tmp")));
    }

    [Fact]
    public void Cleanup_removes_orphan_images_only()
    {
        var store = Store;
        var engine = new HistoryEngine();
        var kept = Guid.NewGuid();
        var dropped = Guid.NewGuid();
        store.SaveImagePng(kept, [1]);
        store.SaveImagePng(dropped, [2]);
        engine.Add(ClipboardEntry.FromImage($"{kept:N}.png", 10, 10, 1, null));

        var removed = store.CleanupOrphanImages(engine);
        Assert.Equal(1, removed);
        Assert.True(File.Exists(Path.Combine(_dir, "cache", $"{kept:N}.png")));
        Assert.False(File.Exists(Path.Combine(_dir, "cache", $"{dropped:N}.png")));
    }

    // ---- 删除即删盘（2026-11-04）：条目移除后，落盘成功 → 排空待删队列 → 文件删除，
    // 与 App.TrySaveAll/SaveHistoryWithImageCleanup 的调用序完全一致 ----

    [Fact]
    public void Removed_entry_image_deleted_after_successful_save()
    {
        var store = Store;
        var engine = new HistoryEngine();
        var id = Guid.NewGuid();
        var name = store.SaveImagePng(id, [9, 9, 9]);
        var entry = ClipboardEntry.FromImage(name, 3, 3, 3, null, null, "abc");
        entry.Id = id; // 与落盘文件名同 Id（生产路径 BuildImage 的约定）
        engine.Add(entry);
        store.Save(engine);
        var path = Path.Combine(_dir, "cache", name);
        Assert.True(File.Exists(path));

        engine.Remove(entry.Id);
        store.Save(engine); // App 侧时序：先落盘（成功）再排空删除
        foreach (var f in engine.DrainPendingImageDeletes()) store.DeleteImageFile(f);

        Assert.False(File.Exists(path));
        // json 不再引用：重启孤儿清理视角下也一致
        Assert.DoesNotContain(new HistoryStore(_dir).Load(), e => e.ImageFile == name);
    }

    [Fact]
    public void DeleteImageFile_ignores_unsafe_names_and_missing_files()
    {
        var store = Store;
        store.DeleteImageFile(@"..\..\evil.png"); // 不得抛、不得越出缓存目录
        store.DeleteImageFile("0123456789abcdef0123456789abcdef.png"); // 合法名但不存在：静默
    }

    [Fact]
    public void Overwrite_save_keeps_single_file()
    {
        var store = Store;
        var engine = new HistoryEngine();
        engine.Add(ClipboardEntry.FromText("v1", null));
        store.Save(engine);
        engine.Add(ClipboardEntry.FromText("v2", null));
        store.Save(engine);
        var files = Directory.GetFiles(_dir, "history.json*");
        Assert.Single(files, f => !f.EndsWith(".corrupt"));
    }

    // ---- 断电/半写健壮性（2026-11-04 审计批次2）：残留 tmp 是明文历史副本，Load 时清掉 ----

    [Fact]
    public void Load_deletes_stale_history_tmp()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "history.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "history.json.tmp"), "写一半被杀的残留（明文历史副本）");
        Assert.Empty(Store.Load());
        Assert.False(File.Exists(Path.Combine(_dir, "history.json.tmp")));
    }

    [Fact]
    public void Save_leaves_no_tmp_and_survives_roundtrip()
    {
        var store = Store;
        var engine = new HistoryEngine();
        engine.Add(ClipboardEntry.FromText("fsync", null));
        store.Save(engine);
        Assert.False(File.Exists(Path.Combine(_dir, "history.json.tmp"))); // fsync+rename 后 tmp 必然消失
        Assert.Single(new HistoryStore(_dir).Load());
    }
}

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pasteup-tests", Guid.NewGuid().ToString("N"));

    private SettingsStore Store => new(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Defaults()
    {
        var s = Store.Load();
        Assert.Equal(ThemeMode.System, s.Theme);
        Assert.Equal("Alt+V", s.InvokeHotkey);
        Assert.Equal("Alt+S", s.ScreenshotHotkey);
        Assert.True(s.PasteOnEnter);
        Assert.Equal(1000, s.HistoryCapacity);
        Assert.Equal(90, s.RetentionDays);
        Assert.False(s.ClearHistoryOnExit);
        Assert.Equal(1.0, s.PinDefaultOpacity);
    }

    [Fact]
    public void Roundtrip_all_fields()
    {
        var store = Store;
        var s = new AppSettings
        {
            Theme = ThemeMode.Dark,
            InvokeHotkey = "Ctrl+Shift+V",
            ScreenshotHotkey = "Win+PrintScreen",
            PasteOnEnter = false,
            HistoryCapacity = 500,
            RetentionDays = 30,
            ClearHistoryOnExit = true,
            PinDefaultOpacity = 0.8,
            PinZoomStepPercent = 20,
            PanelWidth = 800,
            PanelHeight = 700,
            SettingsWidth = 940,
            SettingsHeight = 620,
        };
        store.Save(s);

        var loaded = new SettingsStore(_dir).Load();
        Assert.Equal(ThemeMode.Dark, loaded.Theme);
        Assert.Equal("Ctrl+Shift+V", loaded.InvokeHotkey);
        Assert.Equal("Win+PrintScreen", loaded.ScreenshotHotkey);
        Assert.False(loaded.PasteOnEnter);
        Assert.Equal(500, loaded.HistoryCapacity);
        Assert.Equal(30, loaded.RetentionDays);
        Assert.True(loaded.ClearHistoryOnExit);
        Assert.Equal(0.8, loaded.PinDefaultOpacity);
        Assert.Equal(20, loaded.PinZoomStepPercent);
        // 窗口尺寸记忆（0=未记忆，非 0 必须往返保真）
        Assert.Equal(800, loaded.PanelWidth);
        Assert.Equal(700, loaded.PanelHeight);
        Assert.Equal(940, loaded.SettingsWidth);
        Assert.Equal(620, loaded.SettingsHeight);
    }

    [Fact]
    public void Corrupt_settings_falls_back_to_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{{{ broken");
        var s = Store.Load();
        Assert.Equal("Alt+V", s.InvokeHotkey);
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json.corrupt")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("NotAKey")]
    [InlineData("Alt+虚构键")]
    public void Invalid_hotkey_makes_settings_invalid(string hotkey)
    {
        var s = new AppSettings { InvokeHotkey = hotkey };
        Assert.False(s.IsValid());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100_001)]
    public void Invalid_capacity_rejected(int capacity)
        => Assert.False(new AppSettings { HistoryCapacity = capacity }.IsValid());

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    [InlineData(4000)]
    public void Invalid_retention_rejected(int days)
        => Assert.False(new AppSettings { RetentionDays = days }.IsValid());

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.5)]
    [InlineData(-0.1)]
    public void Invalid_opacity_rejected(double opacity)
        => Assert.False(new AppSettings { PinDefaultOpacity = opacity }.IsValid());

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Invalid_zoom_step_rejected(int step)
        => Assert.False(new AppSettings { PinZoomStepPercent = step }.IsValid());

    [Fact]
    public void Valid_defaults_pass_validation()
        => Assert.True(new AppSettings().IsValid());

    [Fact]
    public void Load_deletes_stale_settings_tmp()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json.tmp"), "residue");
        Assert.Equal("Alt+V", Store.Load().InvokeHotkey);
        Assert.False(File.Exists(Path.Combine(_dir, "settings.json.tmp")));
    }

    [Fact]
    public void Boundary_values_valid()
    {
        var s = new AppSettings { HistoryCapacity = 1, RetentionDays = 1, PinDefaultOpacity = 0.01, PinZoomStepPercent = 100 };
        Assert.True(s.IsValid());
    }
}
