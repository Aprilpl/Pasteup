using System.IO;
using Pasteup.Core.History;
using Xunit;

namespace Pasteup.Tests;

/// <summary>
/// 第4批 P2-3（2026-11-10 复审）：history.json / history.journal Load 的大小与字段上限。
/// history.json 是信任边界（任意进程可篡改/半写）：超大档、超长字段、条目数量爆炸都不得在
/// 启动期 OOM 或抛异常——超限按「损坏档」语义迁 .corrupt 并返回空，字段超限条目丢弃或截断。
/// 纯 Core + 临时目录，不碰 %APPDATA% / 剪贴板。
/// </summary>
public class HistoryLoadLimitsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pasteup-tests", Guid.NewGuid().ToString("N"));

    private string HistoryPath => Path.Combine(_dir, "history.json");
    private string CorruptPath => HistoryPath + ".corrupt";
    private string JournalPath => Path.Combine(_dir, "history.journal");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private HistoryStore NewStore() => new(_dir);

    private static ClipboardEntry Text(string text) => ClipboardEntry.FromText(text, "TestApp");

    /// <summary>用引擎 + 全量 Save 生成真实基档（大字段档不便手写 JSON 时用）。</summary>
    private void SaveBase(params ClipboardEntry[] entries)
    {
        var store = NewStore();
        var engine = new HistoryEngine();
        foreach (var entry in entries) engine.Add(entry);
        store.Save(engine);
    }

    /// <summary>直接写畸形 history.json（Save 路径写不出的重复/空 Id 等信任边界场景）。</summary>
    private void WriteHistory(params string[] entriesJson)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(HistoryPath,
            "{\"Version\":1,\"JournalBase\":null,\"Entries\":[" + string.Join(",", entriesJson) + "]}");
    }

    [Fact]
    public void Default_load_limits_match_design()
    {
        var store = NewStore();
        Assert.Equal(256L * 1024 * 1024, store.MaxHistoryFileBytes);
        Assert.Equal(64L * 1024 * 1024, store.MaxJournalFileBytes);
        Assert.Equal(100_000, store.MaxLoadedEntries);
    }

    // ---- 基档大小上限 ----

    [Fact]
    public void Oversized_history_file_is_quarantined_and_journal_cleared()
    {
        var seed = NewStore();
        var engine = new HistoryEngine();
        engine.Add(Text(new string('a', 4000))); // 基档 > 1KB，保证超过下面的小阈值
        seed.Save(engine);
        engine.Add(Text("journal 新增条目"));
        Assert.True(seed.SaveIncremental(engine));
        Assert.True(File.Exists(JournalPath));
        Assert.True(new FileInfo(HistoryPath).Length > 1024);

        var store = NewStore();
        store.MaxHistoryFileBytes = 1024;
        Assert.Empty(store.Load());

        Assert.True(File.Exists(CorruptPath));   // 按损坏语义留备份
        Assert.False(File.Exists(HistoryPath));  // 原档已迁走
        Assert.False(File.Exists(JournalPath));  // 损坏收尾一并清 journal
    }

    [Fact]
    public void Oversized_history_corrupt_backup_overwrites_previous_and_migrates_once()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(HistoryPath, new string('x', 2048)); // 任意大档：内容无效也先走大小分支
        File.WriteAllText(CorruptPath, "旧备份");

        var store = NewStore();
        store.MaxHistoryFileBytes = 1024;
        Assert.Empty(store.Load());
        Assert.Equal(2048L, new FileInfo(CorruptPath).Length); // 覆盖旧备份，而不是再产生第二个 .corrupt
        Assert.False(File.Exists(HistoryPath));

        // 第二次加载：原档已迁走，按「无基档」返回空，不重复迁移/不抛
        Assert.Empty(NewStore().Load());
        Assert.Equal(2048L, new FileInfo(CorruptPath).Length);
    }

    [Fact]
    public void Too_many_entries_quarantines_file()
    {
        var entries = new string[3];
        for (var i = 0; i < entries.Length; i++)
            entries[i] = $$"""{"Id":"{{Guid.NewGuid()}}","Kind":0,"Preview":"e","FullText":"e{{i}}"}""";
        WriteHistory(entries);

        var store = NewStore();
        store.MaxLoadedEntries = 2; // 反序列化后 3 条 > 上限
        Assert.Empty(store.Load());
        Assert.True(File.Exists(CorruptPath));
        Assert.False(File.Exists(HistoryPath));
    }

    // ---- 条目字段上限 ----

    [Fact]
    public void Entry_with_fulltext_over_limit_is_dropped_others_kept()
    {
        // 10MB（App 入库限额 IngestLimits.MaxTextBytes）+1 字节：Load 侧整条丢弃
        var huge = new string('a', (10 * 1024 * 1024) + 1);
        SaveBase(ClipboardEntry.FromText(huge, "App"), Text("正常条目"));

        var entry = Assert.Single(NewStore().Load());
        Assert.Equal("正常条目", entry.FullText);
    }

    [Fact]
    public void Entry_with_too_many_files_is_dropped()
    {
        var files = new List<string>(1001);
        for (var i = 0; i <= 1000; i++) files.Add($@"C:\f{i}.txt"); // 1001 条 > 上限 1000
        SaveBase(ClipboardEntry.FromFiles(files, "Explorer"), Text("保留"));

        var entry = Assert.Single(NewStore().Load());
        Assert.Equal("保留", entry.FullText);
    }

    [Fact]
    public void Entry_with_overlong_file_path_is_dropped()
    {
        SaveBase(ClipboardEntry.FromFiles([new string('p', 32_768)], "Explorer"), Text("保留"));
        // 单路径 32768 字符 > 上限 32767：整条丢弃（保存侧本身没有路径长度校验，靠 Load 兜底）
        var entry = Assert.Single(NewStore().Load());
        Assert.Equal("保留", entry.FullText);
    }

    [Fact]
    public void Preview_is_truncated_to_200_chars()
    {
        var preview = new string('p', 300);
        SaveBase(new ClipboardEntry
        {
            Id = Guid.NewGuid(),
            Kind = EntryKind.Text,
            Preview = preview,
            FullText = "x",
        });

        var entry = Assert.Single(NewStore().Load());
        Assert.Equal(200, entry.Preview.Length);
        Assert.Equal(preview[..200], entry.Preview);
    }

    [Fact]
    public void Source_app_fields_truncated_and_negative_size_clamped()
    {
        SaveBase(new ClipboardEntry
        {
            Id = Guid.NewGuid(),
            Kind = EntryKind.Text,
            Preview = "x",
            FullText = "x",
            SourceApp = new string('a', 2000),
            SourceAppPath = new string('p', 5000),
            SizeBytes = -5,
        });

        var entry = Assert.Single(NewStore().Load());
        Assert.Equal(1024, entry.SourceApp!.Length);
        Assert.Equal(4096, entry.SourceAppPath!.Length);
        Assert.Equal(0L, entry.SizeBytes); // 负值归 0
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    [InlineData(100_001, 10)]
    [InlineData(10, 100_001)]
    public void Entry_with_invalid_image_dimensions_is_dropped(int width, int height)
    {
        SaveBase(new ClipboardEntry
        {
            Id = Guid.NewGuid(),
            Kind = EntryKind.Image,
            ImageFile = "0123456789abcdef0123456789abcdef.png",
            ImageWidth = width,
            ImageHeight = height,
        });

        Assert.Empty(NewStore().Load());
    }

    [Fact]
    public void Duplicate_and_empty_ids_are_dropped()
    {
        WriteHistory(
            """{"Id":"11111111-1111-1111-1111-111111111111","Kind":0,"Preview":"先出现","FullText":"a"}""",
            """{"Id":"11111111-1111-1111-1111-111111111111","Kind":0,"Preview":"重复","FullText":"b"}""",
            """{"Id":"00000000-0000-0000-0000-000000000000","Kind":0,"Preview":"空 Id","FullText":"c"}""");

        var entry = Assert.Single(NewStore().Load()); // 重复 Id 保留先出现者；空 Id 丢弃
        Assert.Equal("先出现", entry.Preview);
    }

    // ---- journal 大小上限 ----

    [Fact]
    public void Oversized_journal_is_not_replayed_and_next_save_compacts()
    {
        var seed = NewStore();
        var engine = new HistoryEngine();
        engine.Add(Text("基档条目"));
        seed.Save(engine);
        engine.Add(Text("journal 新增条目"));
        Assert.True(seed.SaveIncremental(engine));
        Assert.True(File.Exists(JournalPath));
        Assert.True(new FileInfo(JournalPath).Length > 16);

        // 默认阈值下 journal 正常重放：证明数据本身可重放，下面只有「超阈值」这一个变量
        Assert.Equal(2, NewStore().Load().Count);

        var store = NewStore();
        store.MaxJournalFileBytes = 16; // journal 必然超限
        var loaded = store.Load();      // 只用基档，journal 整份不重放
        var only = Assert.Single(loaded);
        Assert.Equal("基档条目", only.FullText);
        Assert.True(File.Exists(JournalPath)); // 超大 journal 保留（下次 compaction 时清）

        var engine2 = new HistoryEngine();
        engine2.Load(loaded);
        engine2.Add(Text("compaction 之后的新条目"));
        Assert.True(store.SaveIncremental(engine2)); // 超限标记 → 下一次保存必须全量
        Assert.False(File.Exists(JournalPath));      // compaction 已清掉 journal

        var after = NewStore().Load();
        Assert.Equal(2, after.Count);
        Assert.Contains(after, e => e.FullText == "compaction 之后的新条目");
        Assert.DoesNotContain(after, e => e.FullText == "journal 新增条目"); // 超限 journal 未被重放
    }

    // ---- 正常档不受影响 ----

    [Fact]
    public void Normal_small_history_loads_unaffected()
    {
        var seed = NewStore();
        var engine = new HistoryEngine();
        engine.Add(Text("正常一条")); // 单条正常条目：远小于文件/条目/字段上限
        seed.Save(engine);

        var store = NewStore();
        store.MaxHistoryFileBytes = 1024 * 1024;
        var entry = Assert.Single(store.Load());
        Assert.Equal("正常一条", entry.FullText);
        Assert.False(File.Exists(CorruptPath));
    }
}
