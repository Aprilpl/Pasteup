using Pasteup.Core.History;
using Xunit;

namespace Pasteup.Tests;

public class EntryFactoryTests
{
    [Fact]
    public void Plain_text_is_text_kind()
    {
        var e = ClipboardEntry.FromText("普通文本", "Notepad");
        Assert.Equal(EntryKind.Text, e.Kind);
        Assert.Equal("普通文本", e.Preview);
        Assert.Equal("Notepad", e.SourceApp);
    }

    [Theory]
    [InlineData("https://pasteup.dev")]
    [InlineData("www.pasteup.dev/docs")]
    public void Url_becomes_link_kind(string url)
        => Assert.Equal(EntryKind.Link, ClipboardEntry.FromText(url, null).Kind);

    [Fact]
    public void Multiline_text_preview_collapsed()
    {
        var e = ClipboardEntry.FromText("第一行\r\n第二行", null);
        Assert.Equal("第一行 第二行", e.Preview);
        Assert.Equal("第一行\r\n第二行", e.FullText);
    }

    [Fact]
    public void Image_entry_metadata()
    {
        var e = ClipboardEntry.FromImage("a.png", 800, 600, 12_345, "Shot");
        Assert.Equal(EntryKind.Image, e.Kind);
        Assert.Equal(800, e.ImageWidth);
        Assert.Equal(600, e.ImageHeight);
        Assert.Contains("800×600", e.Preview);
        Assert.Contains("12 KB", e.Preview);
    }

    [Fact]
    public void File_entry_joins_paths()
    {
        var e = ClipboardEntry.FromFiles([@"C:\a.txt", @"C:\b\c.png"], "Explorer");
        Assert.Equal(EntryKind.File, e.Kind);
        Assert.Equal(2, e.Files.Count);
        Assert.Contains("a.txt", e.Preview);
        Assert.Contains("c.png", e.Preview);
    }

    [Fact]
    public void Content_key_equal_for_same_text()
    {
        var a = ClipboardEntry.FromText("同内容", null);
        var b = ClipboardEntry.FromText("同内容", "别的应用");
        Assert.Equal(a.ContentKey, b.ContentKey);
    }

    [Fact]
    public void Content_key_differs_between_link_and_text_of_same_string()
    {
        var link = ClipboardEntry.FromText("https://x.dev", null); // Link
        var plain = new ClipboardEntry
        {
            Id = Guid.NewGuid(),
            Kind = EntryKind.Text,
            Preview = "https://x.dev",
            FullText = "https://x.dev",
        };
        Assert.NotEqual(link.ContentKey, plain.ContentKey);
    }

    [Fact]
    public void Content_key_for_files_is_order_insensitive()
    {
        var a = ClipboardEntry.FromFiles([@"C:\1.txt", @"C:\2.txt"], null);
        var b = ClipboardEntry.FromFiles([@"C:\2.txt", @"C:\1.txt"], null);
        Assert.Equal(a.ContentKey, b.ContentKey);
    }

    [Fact]
    public void Content_key_for_image_ignores_random_filename()
    {
        var a = ClipboardEntry.FromImage("x.png", 800, 600, 100, null);
        var b = ClipboardEntry.FromImage("y.png", 800, 600, 100, null); // 文件名每次随机生成，不得参与键
        var c = ClipboardEntry.FromImage("y.png", 801, 600, 100, null);
        var d = ClipboardEntry.FromImage("y.png", 800, 600, 101, null);
        Assert.Equal(a.ContentKey, b.ContentKey);
        Assert.NotEqual(a.ContentKey, c.ContentKey);
        Assert.NotEqual(a.ContentKey, d.ContentKey);
    }
}

public class HistoryEngineTests
{
    private static ClipboardEntry Text(string s, int minutesAgo = 0) =>
        new()
        {
            Id = Guid.NewGuid(),
            Kind = EntryKind.Text,
            Preview = s,
            FullText = s,
            UtcTimestamp = DateTime.UtcNow.AddMinutes(-minutesAgo),
        };

    [Fact]
    public void Add_puts_newest_first()
    {
        var engine = new HistoryEngine();
        engine.Add(Text("a"));
        engine.Add(Text("b"));
        Assert.Equal("b", engine.Snapshot()[0].Preview);
        Assert.Equal(2, engine.Count);
    }

    [Fact]
    public void Consecutive_duplicate_collapses_to_one()
    {
        var engine = new HistoryEngine();
        engine.Add(Text("同"));
        engine.Add(Text("同"));
        Assert.Equal(1, engine.Count);
    }

    [Fact]
    public void Older_duplicate_moves_to_front()
    {
        var engine = new HistoryEngine();
        engine.Add(Text("x"));
        engine.Add(Text("y"));
        engine.Add(Text("x"));
        Assert.Equal(2, engine.Count);
        Assert.Equal("x", engine.Snapshot()[0].Preview);
    }

    private static ClipboardEntry Image(string file, int w, int h, long bytes) =>
        ClipboardEntry.FromImage(file, w, h, bytes, null);

    [Fact]
    public void Re_copied_image_dedups_by_content_not_filename()
    {
        var engine = new HistoryEngine();
        engine.Add(Image("a.png", 800, 600, 100));
        engine.Add(Text("中间插一条"));
        engine.Add(Image("b.png", 800, 600, 100)); // 同一张图：同尺寸同字节数，仅随机文件名不同
        Assert.Equal(2, engine.Count);
        Assert.Equal(EntryKind.Image, engine.Snapshot()[0].Kind); // 顶到最上
        Assert.Equal("b.png", engine.Snapshot()[0].ImageFile);
    }

    [Fact]
    public void Re_copied_entry_keeps_pinned_state()
    {
        var engine = new HistoryEngine();
        var first = engine.Add(Text("p"));
        engine.TogglePin(first.Id);
        engine.Add(Text("p"));
        Assert.Single(engine.Snapshot());
        Assert.True(engine.Snapshot()[0].Pinned);
    }

    // ---- 收藏：独立双态标志，只打标记不动顺序，豁免淘汰/过期/清空 ----

    [Fact]
    public void ToggleFavorite_flips_flag_without_reordering()
    {
        var engine = new HistoryEngine();
        var a = engine.Add(Text("甲"));
        var b = engine.Add(Text("乙"));
        engine.Add(Text("丙"));
        // 收藏最旧一条：标志翻转但位置不动（与置顶「移到最前」的关键差异）
        Assert.True(engine.ToggleFavorite(a.Id));
        Assert.Equal(["丙", "乙", "甲"], engine.Snapshot().Select(e => e.Preview).ToArray());
        Assert.False(engine.ToggleFavorite(a.Id));
        Assert.False(engine.Snapshot()[2].Favorite);
    }

    [Fact]
    public void ToggleFavorite_missing_id_returns_false()
    {
        var engine = new HistoryEngine();
        Assert.False(engine.ToggleFavorite(Guid.NewGuid()));
    }

    [Fact]
    public void Re_copied_entry_keeps_favorite_state()
    {
        var engine = new HistoryEngine();
        var first = engine.Add(Text("p"));
        engine.ToggleFavorite(first.Id);
        engine.Add(Text("p"));
        Assert.Single(engine.Snapshot());
        Assert.True(engine.Snapshot()[0].Favorite);
    }

    [Fact]
    public void Favorite_exempt_from_capacity_trim()
    {
        var engine = new HistoryEngine { Capacity = 3 };
        for (var i = 0; i < 4; i++) engine.Add(Text($"e{i}"));
        var oldest = engine.Snapshot()[^1];
        engine.ToggleFavorite(oldest.Id); // 最旧的加收藏
        for (var i = 4; i < 6; i++) engine.Add(Text($"e{i}"));
        // 非置顶非收藏超容后只数 3；收藏条目豁免，总数 = 3 + 1
        Assert.Equal(4, engine.Count);
        Assert.Contains(engine.Snapshot(), e => e.Favorite);
    }

    [Fact]
    public void Favorite_and_pinned_both_exempt_together()
    {
        var engine = new HistoryEngine { Capacity = 2 };
        var fav = engine.Add(Text("收藏"));
        engine.ToggleFavorite(fav.Id);
        var pin = engine.Add(Text("置顶"));
        engine.TogglePin(pin.Id);
        for (var i = 0; i < 4; i++) engine.Add(Text($"e{i}"));
        Assert.Equal(4, engine.Count); // 2 容量 + 收藏 1 + 置顶 1，两标志互不挤占
    }

    [Fact]
    public void Retention_days_keeps_favorite()
    {
        var engine = new HistoryEngine { RetentionDays = 1, Capacity = 100 };
        var old = Text("旧收藏", minutesAgo: 60 * 24 * 3);
        old.Favorite = true; // Load 即触发 TrimLocked，收藏须先置位（Add 会被同款裁剪拦在门外）
        engine.Load([old, Text("新鲜")]);
        Assert.Equal(2, engine.Count);
    }

    [Fact]
    public void Clear_keeps_favorite_by_default()
    {
        var engine = new HistoryEngine();
        var a = engine.Add(Text("甲"));
        engine.Add(Text("乙"));
        engine.ToggleFavorite(a.Id);
        engine.Clear();
        Assert.Single(engine.Snapshot());
        Assert.True(engine.Snapshot()[0].Favorite);
    }

    [Fact]
    public void Clear_all_removes_favorite_too()
    {
        var engine = new HistoryEngine();
        var a = engine.Add(Text("甲"));
        engine.ToggleFavorite(a.Id);
        engine.Clear(includePinned: true);
        Assert.Empty(engine.Snapshot());
    }

    [Fact]
    public void Capacity_trims_oldest_unpinned()
    {
        var engine = new HistoryEngine { Capacity = 3 };
        for (var i = 0; i < 5; i++) engine.Add(Text($"e{i}"));
        Assert.Equal(3, engine.Count);
        Assert.Equal("e4", engine.Snapshot()[0].Preview); // 最新的保留
    }

    [Fact]
    public void Pinned_exempt_from_capacity_trim()
    {
        var engine = new HistoryEngine { Capacity = 3 };
        var p0 = engine.Add(Text("老置顶"));
        engine.TogglePin(p0.Id);
        for (var i = 0; i < 5; i++) engine.Add(Text($"e{i}"));
        // 未置顶恰好 3 条（容量语义），置顶 1 条额外保留 → 总计 4
        Assert.Equal(4, engine.Count);
        Assert.Equal(3, engine.Snapshot().Count(e => !e.Pinned));
        Assert.Contains(engine.Snapshot(), e => e.Pinned);
    }

    [Fact]
    public void Retention_days_drops_old_unpinned()
    {
        var engine = new HistoryEngine { RetentionDays = 1, Capacity = 100 };
        var old = Text("过期", minutesAgo: 60 * 24 * 3);
        engine.Load([old, Text("新鲜")]);
        Assert.Single(engine.Snapshot());
    }

    [Fact]
    public void Retention_days_keeps_old_pinned()
    {
        var engine = new HistoryEngine { RetentionDays = 1 };
        var old = Text("过期置顶", minutesAgo: 60 * 24 * 30);
        old.Pinned = true; // Load 时即置顶，裁剪豁免
        engine.Load([old]);
        Assert.Single(engine.Snapshot());
    }

    [Fact]
    public void Clear_keeps_pinned_by_default()
    {
        var engine = new HistoryEngine();
        var p = engine.Add(Text("留"));
        engine.TogglePin(p.Id);
        engine.Add(Text("删"));
        engine.Clear();
        Assert.Single(engine.Snapshot());
    }

    [Fact]
    public void Clear_all_removes_pinned_too()
    {
        var engine = new HistoryEngine();
        var p = engine.Add(Text("也删"));
        engine.TogglePin(p.Id);
        engine.Clear(includePinned: true);
        Assert.Empty(engine.Snapshot());
    }

    [Fact]
    public void Remove_by_id()
    {
        var engine = new HistoryEngine();
        var e = engine.Add(Text("目标"));
        Assert.True(engine.Remove(e.Id));
        Assert.Empty(engine.Snapshot());
        Assert.False(engine.Remove(e.Id));
    }

    // ---- 删除即删盘：条目被移除后其图像文件名记入待删队列，调用方在落盘成功后排空删除 ----

    [Fact]
    public void Remove_tracks_image_file_for_disk_delete()
    {
        var engine = new HistoryEngine();
        var img = engine.Add(Image("a.png", 10, 10, 100));
        Assert.True(engine.Remove(img.Id));
        Assert.Equal(["a.png"], engine.DrainPendingImageDeletes());
        Assert.Empty(engine.DrainPendingImageDeletes()); // 排空后不重复给
    }

    [Fact]
    public void Remove_text_entry_tracks_nothing()
    {
        var engine = new HistoryEngine();
        var e = engine.Add(Text("文本"));
        engine.Remove(e.Id);
        Assert.Empty(engine.DrainPendingImageDeletes());
    }

    [Fact]
    public void Clear_tracks_unkept_image_files_only()
    {
        var engine = new HistoryEngine();
        var keep = engine.Add(Image("keep.png", 10, 10, 100));
        engine.TogglePin(keep.Id);
        engine.Add(Image("gone.png", 11, 10, 100));
        engine.Clear(); // 默认保留置顶/收藏
        Assert.Equal(["gone.png"], engine.DrainPendingImageDeletes());
    }

    [Fact]
    public void Capacity_trim_tracks_evicted_image_file()
    {
        var engine = new HistoryEngine { Capacity = 1 };
        engine.Add(Image("old.png", 10, 10, 100));
        engine.Add(Text("新")); // 容量 1：old.png 被裁剪淘汰
        Assert.Equal(["old.png"], engine.DrainPendingImageDeletes());
    }

    [Fact]
    public void Retention_trim_tracks_expired_image_file()
    {
        var engine = new HistoryEngine { RetentionDays = 1, Capacity = 100 };
        var old = Image("expired.png", 10, 10, 100);
        old.UtcTimestamp = DateTime.UtcNow.AddDays(-3);
        engine.Load([old]); // Load 即触发 TrimLocked
        Assert.Empty(engine.Snapshot());
        Assert.Equal(["expired.png"], engine.DrainPendingImageDeletes());
    }

    [Fact]
    public void Dedup_replacement_tracks_superseded_image_file()
    {
        var engine = new HistoryEngine { Capacity = 100 };
        engine.Add(Image("old.png", 10, 10, 100));
        engine.Add(Image("new.png", 10, 10, 100)); // 同内容（弱键）顶掉 old.png
        Assert.Single(engine.Snapshot());
        Assert.Equal(["old.png"], engine.DrainPendingImageDeletes());
    }

    [Fact]
    public void Load_does_not_track_replaced_entries()
    {
        var engine = new HistoryEngine();
        engine.Add(Image("a.png", 10, 10, 100));
        engine.Load([Text("换血")]); // Load 整体替换由启动期孤儿清理兜底，不入待删队列
        Assert.Empty(engine.DrainPendingImageDeletes());
    }

    [Fact]
    public void Same_size_same_bytes_different_hash_keeps_both()
    {
        var engine = new HistoryEngine();
        engine.Add(ClipboardEntry.FromImage("a.png", 800, 600, 1000, null, null, "1111111111111111"));
        engine.Add(ClipboardEntry.FromImage("b.png", 800, 600, 1000, null, null, "2222222222222222"));
        Assert.Equal(2, engine.Count); // 旧弱键时代：不同内容的图互顶、旧条目静默丢失
    }

    [Fact]
    public void Touch_refreshes_timestamp_and_lands_after_pinned_prefix()
    {
        var engine = new HistoryEngine();
        engine.Add(Text("旧甲", minutesAgo: 60));
        var a = engine.Add(Text("旧乙", minutesAgo: 120));
        // Add 无条件插头部，故置顶操作放最后，保证置顶条目位于列表前缀
        var p = engine.Add(Text("钉住", minutesAgo: 5));
        engine.TogglePin(p.Id);

        Assert.True(engine.Touch(a.Id));
        var snap = engine.Snapshot();
        // 置顶区恒在最前，取用条目跳到其后的第一位
        Assert.Equal(["钉住", "旧乙", "旧甲"], snap.Select(e => e.Preview).ToArray());
        // Id 不变（图像缓存文件仍有效）、时间戳刷新为取用时刻
        Assert.Equal(a.Id, snap[1].Id);
        Assert.True(snap[1].UtcTimestamp > DateTime.UtcNow.AddSeconds(-30));
    }

    [Fact]
    public void Touch_keeps_pinned_flag_and_stays_first()
    {
        var engine = new HistoryEngine();
        var e = engine.Add(Text("既钉"));
        engine.TogglePin(e.Id);
        engine.Add(Text("新的"));

        Assert.True(engine.Touch(e.Id));
        var snap = engine.Snapshot();
        Assert.Equal(["既钉", "新的"], snap.Select(x => x.Preview).ToArray());
        Assert.True(snap[0].Pinned);
        Assert.Equal(e.Id, snap[0].Id);
    }

    [Fact]
    public void Touch_missing_id_returns_false()
    {
        var engine = new HistoryEngine();
        engine.Add(Text("在"));
        Assert.False(engine.Touch(Guid.NewGuid()));
        Assert.Equal(["在"], engine.Snapshot().Select(e => e.Preview).ToArray());
    }

    [Fact]
    public void TogglePin_moves_entry_to_front()
    {
        var engine = new HistoryEngine();
        var a = engine.Add(Text("a"));
        engine.Add(Text("b"));
        engine.Add(Text("c"));
        engine.TogglePin(a.Id);
        Assert.Equal("a", engine.Snapshot()[0].Preview);
        engine.TogglePin(a.Id);
        var unpinned = engine.Snapshot();
        // 取消置顶：标志翻回但位置不动（引擎不回移，下次 Add/Load 才回到时序位）——
        // 原恒真断言（A||恒真B）从未锁定过此行为，真实序 = 置顶时移到最前的 [a,c,b] 原样保留
        Assert.Equal(["a", "c", "b"], unpinned.Select(e => e.Preview).ToArray());
        Assert.False(unpinned[0].Pinned);
    }

    [Fact]
    public void Query_filters_by_kind()
    {
        var engine = new HistoryEngine();
        engine.Add(ClipboardEntry.FromText("https://a.dev", null)); // Link
        engine.Add(Text("纯文本"));
        Assert.Single(engine.Query(EntryKind.Link));
        Assert.Single(engine.Query(EntryKind.Text));
        Assert.Equal(2, engine.Query().Count);
    }

    [Theory]
    [InlineData("Pasteup")]
    [InlineData("pasteup")]
    [InlineData("PASTEUP")]
    public void Query_search_is_case_insensitive(string needle)
    {
        var engine = new HistoryEngine();
        engine.Add(Text("欢迎使用 Pasteup"));
        Assert.Single(engine.Query(search: needle));
    }

    [Fact]
    public void Query_search_hits_fulltext_not_in_preview()
    {
        var engine = new HistoryEngine();
        engine.Add(new ClipboardEntry
        {
            Id = Guid.NewGuid(),
            Kind = EntryKind.Text,
            Preview = "截断…",
            FullText = "尾部隐藏着 needle 词",
        });
        Assert.Single(engine.Query(search: "needle"));
    }

    [Fact]
    public void Query_search_hits_files()
    {
        var engine = new HistoryEngine();
        engine.Add(ClipboardEntry.FromFiles([@"C:\报告\季度.xlsx"], null));
        Assert.Single(engine.Query(search: "季度"));
    }

    [Fact]
    public void Query_search_scans_fulltext_head_only()
    {
        // 大文本只搜头部 64K（截断命中足够定位）：尾部词不命中，头部词照常
        var engine = new HistoryEngine();
        engine.Add(new ClipboardEntry
        {
            Id = Guid.NewGuid(),
            Kind = EntryKind.Text,
            Preview = "截断…",
            FullText = new string('x', 70_000) + "藏在尾部",
        });
        Assert.Empty(engine.Query(search: "藏在尾部"));
        Assert.Single(engine.Query(search: "x"));
    }

    [Fact]
    public void Query_filter_pinned_and_today()
    {
        var engine = new HistoryEngine();
        var pinned = engine.Add(Text("置顶条目"));
        engine.TogglePin(pinned.Id);
        engine.Add(Text("今天条目"));

        var pinnedOnly = engine.Query(filter: e => e.Pinned);
        Assert.Single(pinnedOnly);
        Assert.Equal("置顶条目", pinnedOnly[0].Preview);

        var todayOnly = engine.Query(filter: e => e.UtcTimestamp.ToLocalTime() >= DateTime.Now.Date);
        Assert.Equal(2, todayOnly.Count);

        // 过滤与搜索可叠加
        Assert.Single(engine.Query(search: "置顶", filter: e => e.Pinned));
        Assert.Empty(engine.Query(search: "置顶", filter: e => !e.Pinned));
    }

    [Fact]
    public void Counts_by_kind()
    {
        var engine = new HistoryEngine();
        engine.Add(Text("a"));
        engine.Add(Text("b"));
        engine.Add(ClipboardEntry.FromText("https://c.dev", null));
        var counts = engine.CountsByKind();
        Assert.Equal(2, counts[EntryKind.Text]);
        Assert.Equal(1, counts[EntryKind.Link]);
        Assert.Equal(0, counts[EntryKind.Image]);
        Assert.Equal(0, counts[EntryKind.File]);
    }

    [Fact]
    public void Changed_event_fires_on_add()
    {
        var engine = new HistoryEngine();
        var fired = 0;
        engine.Changed += () => fired++;
        engine.Add(Text("x"));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Each_mutator_fires_changed_exactly_once()
    {
        // Changed 挪到锁外触发（2026-11-06 性能复审⑦）后的契约网：每个变更恰好一次事件、
        // Version 与事件数同步——重构中漏提/多提 Invoke 都会在此现形。
        var engine = new HistoryEngine();
        var fired = 0;
        engine.Changed += () => fired++;
        engine.Add(Text("a"));
        var id = engine.Snapshot()[0].Id;
        engine.Touch(id);
        engine.TogglePin(id);
        engine.ToggleFavorite(id);
        engine.Remove(id);
        engine.Add(Text("b"));
        engine.Clear();
        Assert.Equal(7, fired);
        Assert.Equal(7L, engine.Version);
    }

    [Fact]
    public void Load_orders_by_time_descending()
    {
        var engine = new HistoryEngine();
        engine.Load([Text("旧", minutesAgo: 60), Text("新", minutesAgo: 1)]);
        Assert.Equal("新", engine.Snapshot()[0].Preview);
    }

    [Fact]
    public void Load_keeps_pinned_entries_as_prefix()
    {
        var engine = new HistoryEngine();
        var pinnedOld = Text("旧置顶", minutesAgo: 60 * 24);
        pinnedOld.Pinned = true;
        engine.Load([Text("新", minutesAgo: 1), pinnedOld, Text("中", minutesAgo: 60)]);
        var snap = engine.Snapshot();
        // 置顶条目最旧仍居首：运行期 Touch（跳前导置顶段）与分组渲染（连续段判组界）都依赖此前缀，
        // 只按时间排序会把旧置顶散落中部——重启后「今天→置顶→今天」组头交错
        Assert.Equal("旧置顶", snap[0].Preview);
        Assert.Equal(["新", "中"], snap.Skip(1).Select(e => e.Preview).ToArray());
    }
}

public class EntryGroupingTests
{
    [Fact]
    public void Pinned_gets_own_group()
    {
        var pinned = Text("置顶项");
        pinned.Pinned = true;
        var result = EntryGrouping.Classify([pinned], DateTime.Now);
        Assert.Equal(EntryAgeGroup.Pinned, result[0].Group);
    }

    [Fact]
    public void Today_yesterday_week_earlier()
    {
        var now = DateTime.Now;
        var entries = new[]
        {
            Text("t", 0), Text("y", 1441), Text("w", 3 * 1440), Text("e", 20 * 1440),
        };
        var result = EntryGrouping.Classify(entries, now)
            .ToDictionary(x => x.Item2.Preview, x => x.Group);
        Assert.Equal(EntryAgeGroup.Today, result["t"]);
        Assert.Equal(EntryAgeGroup.Yesterday, result["y"]);
        Assert.Equal(EntryAgeGroup.ThisWeek, result["w"]);
        Assert.Equal(EntryAgeGroup.Earlier, result["e"]);
    }

    [Theory]
    [InlineData(EntryAgeGroup.Pinned, "置顶")]
    [InlineData(EntryAgeGroup.Today, "今天")]
    [InlineData(EntryAgeGroup.Yesterday, "昨天")]
    [InlineData(EntryAgeGroup.ThisWeek, "本周")]
    [InlineData(EntryAgeGroup.Earlier, "更早")]
    public void Labels(EntryAgeGroup g, string expected)
        => Assert.Equal(expected, EntryGrouping.GroupLabel(g));

    private static ClipboardEntry Text(string preview, int minutesAgo = 0) =>
        new()
        {
            Id = Guid.NewGuid(),
            Kind = EntryKind.Text,
            Preview = preview,
            UtcTimestamp = DateTime.UtcNow.AddMinutes(-minutesAgo),
        };
}
