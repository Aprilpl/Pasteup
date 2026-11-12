using System.IO;
using Pasteup.Core.History;
using Xunit;

namespace Pasteup.Tests;

/// <summary>
/// 第3批 P1-1（复审「其他#1」）：history.json 增量保存（journal + 全量 compaction）。
/// 纯 Core + 临时目录；不碰 %APPDATA%/剪贴板。核心不变量：增量追加后，新 store 的 Load
/// 结果必须与引擎快照「内容 + 顺序」逐字段一致；删除/清空必须强制全量且 journal 清空。
/// </summary>
public class IncrementalSaveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pasteup-tests", Guid.NewGuid().ToString("N"));

    private string HistoryPath => Path.Combine(_dir, "history.json");
    private string JournalPath => Path.Combine(_dir, "history.journal");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private HistoryStore NewStore() => new(_dir);

    private static ClipboardEntry Text(string text) => ClipboardEntry.FromText(text, "TestApp");

    /// <summary>逐条比较引擎快照（顺序 + 全部持久化字段）与 Load 结果。</summary>
    private static void AssertSameAsEngine(HistoryEngine engine, List<ClipboardEntry> loaded)
    {
        var snap = engine.Snapshot();
        Assert.Equal(snap.Count, loaded.Count);
        for (var i = 0; i < snap.Count; i++)
        {
            var a = snap[i];
            var b = loaded[i];
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Kind, b.Kind);
            Assert.Equal(a.Preview, b.Preview);
            Assert.Equal(a.FullText, b.FullText);
            Assert.Equal(a.ImageFile, b.ImageFile);
            Assert.Equal(a.ImageWidth, b.ImageWidth);
            Assert.Equal(a.ImageHeight, b.ImageHeight);
            Assert.Equal(a.Files, b.Files);
            Assert.Equal(a.SourceApp, b.SourceApp);
            Assert.Equal(a.SourceAppPath, b.SourceAppPath);
            Assert.Equal(a.Pinned, b.Pinned);
            Assert.Equal(a.Favorite, b.Favorite);
            Assert.Equal(a.UtcTimestamp.Ticks, b.UtcTimestamp.Ticks); // 序列化往返必须保持 tick 精度
            Assert.Equal(a.SizeBytes, b.SizeBytes);
            Assert.Equal(a.ContentHash, b.ContentHash);
        }
    }

    [Fact]
    public void Default_thresholds_match_design()
    {
        var store = NewStore();
        Assert.Equal(8L * 1024 * 1024, store.JournalMaxBytes);
        Assert.Equal(256, store.JournalMaxRecords);
    }

    [Fact]
    public void Full_save_creates_base_and_clears_journal()
    {
        var store = NewStore();
        var engine = new HistoryEngine();
        engine.Add(Text("甲"));
        engine.Add(Text("乙"));

        store.Save(engine);

        Assert.True(File.Exists(HistoryPath));
        Assert.False(File.Exists(JournalPath)); // 全量保存后 journal 必须清空
        AssertSameAsEngine(engine, NewStore().Load());
    }

    [Fact]
    public void Incremental_add_appends_journal_and_new_store_loads_same()
    {
        var store = NewStore();
        var engine = new HistoryEngine();
        engine.Add(Text("甲"));
        engine.Add(Text("乙"));
        store.Save(engine);

        engine.Add(Text("丙"));
        Assert.True(store.SaveIncremental(engine));

        Assert.True(File.Exists(JournalPath)); // 纯新增：只追加 journal，不重写基档
        AssertSameAsEngine(engine, NewStore().Load());

        // 再显式全量保存一次：journal 清空、Load 仍一致
        store.Save(engine);
        Assert.False(File.Exists(JournalPath));
        AssertSameAsEngine(engine, NewStore().Load());
    }

    [Fact]
    public void Multiple_increments_with_reorder_and_flags_load_same()
    {
        var store = NewStore();
        var engine = new HistoryEngine();
        var a = engine.Add(Text("甲"));
        var b = engine.Add(Text("乙"));
        var c = engine.Add(Text("丙"));
        store.Save(engine);

        engine.Add(Text("丁"));
        Assert.True(store.SaveIncremental(engine));   // 新增
        engine.Touch(a.Id);                            // 重排 + 时间戳更新
        Assert.True(store.SaveIncremental(engine));
        engine.TogglePin(b.Id);                        // 置顶 + 移到最前
        Assert.True(store.SaveIncremental(engine));
        engine.ToggleFavorite(c.Id);                   // 收藏（只翻标志）
        Assert.True(store.SaveIncremental(engine));

        AssertSameAsEngine(engine, NewStore().Load());
    }

    [Fact]
    public void Removal_forces_full_save_and_clears_journal()
    {
        var store = NewStore();
        var engine = new HistoryEngine();
        var a = engine.Add(Text("甲"));
        var b = engine.Add(Text("乙"));
        store.Save(engine);
        engine.Add(Text("丙"));
        Assert.True(store.SaveIncremental(engine));
        Assert.True(File.Exists(JournalPath));

        engine.Remove(b.Id);
        Assert.True(store.SaveIncremental(engine));

        Assert.False(File.Exists(JournalPath)); // 删除必须全量且清 journal
        var loaded = NewStore().Load();
        Assert.DoesNotContain(loaded, e => e.Id == b.Id); // 已删条目不得复活
        Assert.Contains(loaded, e => e.Id == a.Id);
        AssertSameAsEngine(engine, loaded);
    }

    [Fact]
    public void Clear_forces_full_save_and_no_resurrection()
    {
        var store = NewStore();
        var engine = new HistoryEngine();
        engine.Add(Text("甲"));
        engine.Add(Text("乙"));
        store.Save(engine);
        engine.Add(Text("丙"));
        Assert.True(store.SaveIncremental(engine));

        engine.Clear();
        Assert.True(store.SaveIncremental(engine));

        Assert.False(File.Exists(JournalPath));
        Assert.Empty(NewStore().Load());
    }

    [Fact]
    public void Tail_partial_line_tolerated_and_next_save_compacts()
    {
        var store = NewStore();
        var engine = new HistoryEngine();
        engine.Add(Text("甲"));
        engine.Add(Text("乙"));
        store.Save(engine);
        engine.Add(Text("丙"));
        Assert.True(store.SaveIncremental(engine));

        // 模拟写 journal 一半被杀：追加无换行的半行 JSON
        File.AppendAllText(JournalPath, "{\"Base\":\"");

        var store2 = NewStore();
        var loaded = store2.Load(); // 不抛、不整档变 .corrupt
        AssertSameAsEngine(engine, loaded); // 得到最后一个完整记录的状态
        Assert.True(File.Exists(JournalPath)); // 坏行保留（下次保存全量重写）

        // 坏尾之后的第一次保存必须全量重写 journal（否则新记录会被坏行截断而无法重放）
        var engine2 = new HistoryEngine();
        engine2.Load(loaded);
        engine2.Add(Text("新的一条"));
        Assert.True(store2.SaveIncremental(engine2));

        Assert.False(File.Exists(JournalPath));
        AssertSameAsEngine(engine2, NewStore().Load());
    }

    [Fact]
    public void Journal_record_threshold_triggers_compaction()
    {
        var store = NewStore();
        store.JournalMaxRecords = 1;
        var engine = new HistoryEngine();
        engine.Add(Text("甲"));
        store.Save(engine);

        engine.Add(Text("乙"));
        Assert.True(store.SaveIncremental(engine)); // 0 条 < 阈值 1 → 追加
        Assert.True(File.Exists(JournalPath));

        engine.Add(Text("丙"));
        Assert.True(store.SaveIncremental(engine)); // 已达阈值 → 全量 compaction

        Assert.False(File.Exists(JournalPath));
        AssertSameAsEngine(engine, NewStore().Load());
    }

    [Fact]
    public void Journal_bytes_threshold_triggers_compaction()
    {
        var store = NewStore();
        store.JournalMaxBytes = 1;
        var engine = new HistoryEngine();
        engine.Add(Text("甲"));
        store.Save(engine);

        engine.Add(Text("乙"));
        Assert.True(store.SaveIncremental(engine)); // journal 原本不存在 → 先追加
        Assert.True(File.Exists(JournalPath));

        engine.Add(Text("丙"));
        Assert.True(store.SaveIncremental(engine)); // 字节数已超阈值 → 全量 compaction

        Assert.False(File.Exists(JournalPath));
        AssertSameAsEngine(engine, NewStore().Load());
    }

    [Fact]
    public void SaveIncremental_without_changes_does_not_append_journal()
    {
        var store = NewStore();
        var engine = new HistoryEngine();
        engine.Add(Text("甲"));
        store.Save(engine);
        var baseBefore = File.ReadAllBytes(HistoryPath);

        Assert.True(store.SaveIncremental(engine)); // 无变更（例如仅设置变更触发的保存）

        Assert.False(File.Exists(JournalPath)); // 不追加空记录（避免 journal 白涨记录数/字节数）
        Assert.Equal(baseBefore, File.ReadAllBytes(HistoryPath));
    }

    [Fact]
    public void Journal_is_much_smaller_than_full_base()
    {
        var store = NewStore();
        var engine = new HistoryEngine { Capacity = 5000 };
        for (var i = 0; i < 1000; i++)
            engine.Add(ClipboardEntry.FromText($"第{i}条 {new string('x', 80)}", "App"));
        store.Save(engine);
        var baseBytes = new FileInfo(HistoryPath).Length;

        engine.Add(ClipboardEntry.FromText("新增的一条", "App"));
        Assert.True(store.SaveIncremental(engine));

        var journalBytes = new FileInfo(JournalPath).Length;
        Assert.True(journalBytes * 2 < baseBytes,
            $"增量 journal 应明显小于全量基档：journal={journalBytes}B base={baseBytes}B");
        AssertSameAsEngine(engine, NewStore().Load());
    }

    [Fact]
    public void Load_initializes_snapshot_so_next_save_keeps_base_untouched()
    {
        var store = NewStore();
        var engine = new HistoryEngine();
        engine.Add(Text("甲"));
        engine.Add(Text("乙"));
        store.Save(engine);
        var baseBefore = File.ReadAllBytes(HistoryPath);

        // 模拟重启：新 store 先 Load（初始化 _lastSaved 快照）
        var store2 = NewStore();
        var engine2 = new HistoryEngine();
        engine2.Load(store2.Load());
        engine2.Add(Text("丙"));
        Assert.True(store2.SaveIncremental(engine2));

        Assert.Equal(baseBefore, File.ReadAllBytes(HistoryPath)); // 基档一字未动
        Assert.True(File.Exists(JournalPath));
        AssertSameAsEngine(engine2, NewStore().Load());
    }

    [Fact]
    public void Stale_journal_from_previous_generation_ignored()
    {
        var store = NewStore();
        var engine = new HistoryEngine();
        engine.Add(Text("甲"));
        store.Save(engine);
        engine.Add(Text("乙"));
        Assert.True(store.SaveIncremental(engine));
        var staleJournal = File.ReadAllBytes(JournalPath); // 旧基档代号 + 乙

        store.Save(engine); // 全量 compaction：新基档换代号、journal 清空
        Assert.False(File.Exists(JournalPath));
        File.WriteAllBytes(JournalPath, staleJournal); // 模拟「新基档写好后、清 journal 前崩溃」的残留

        var loaded = NewStore().Load();

        AssertSameAsEngine(engine, loaded);      // 旧 journal 不得被重放（不复活已删条目/不退回旧顺序）
        Assert.False(File.Exists(JournalPath));  // Load 发现代号不匹配后清掉它
    }

    [Fact]
    public void Base_missing_with_journal_returns_empty_and_drops_journal()
    {
        var store = NewStore();
        var engine = new HistoryEngine();
        engine.Add(Text("甲"));
        store.Save(engine);
        engine.Add(Text("乙"));
        Assert.True(store.SaveIncremental(engine));
        Assert.True(File.Exists(JournalPath));

        File.Delete(HistoryPath); // 基档丢失：journal 只是差量，无法独立重建

        var loaded = NewStore().Load();
        Assert.Empty(loaded);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Incremental_without_any_saved_state_writes_full_base()
    {
        var store = NewStore();
        var engine = new HistoryEngine();
        engine.Add(Text("甲"));

        Assert.True(store.SaveIncremental(engine)); // 无基档/无快照 → 内部自动全量

        Assert.True(File.Exists(HistoryPath));
        Assert.False(File.Exists(JournalPath));
        AssertSameAsEngine(engine, NewStore().Load());
    }
}
