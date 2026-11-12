using Pasteup.App.ViewModels;
using Pasteup.Core.History;
using Pasteup.Core.Text;
using Xunit;

namespace Pasteup.Tests;

/// <summary>
/// 拼接差量更新（RefreshFromEngine：键序列对齐 → 只 materialize 变化中段）：未变行的 VM 实例必须原样保留
///（容器不换=列表无瞬态布局，点击不被重建吃掉），易变字段原地刷新。
/// </summary>
public class SpliceUpdateTests
{
    private static ClipboardEntry Text(string s, int minutesAgo = 0) => new()
    {
        Id = Guid.NewGuid(),
        Kind = EntryKind.Text,
        Preview = s,
        FullText = s,
        UtcTimestamp = DateTime.UtcNow.AddMinutes(-minutesAgo),
    };

    private static (PanelViewModel Vm, HistoryEngine Engine) VmWith(params ClipboardEntry[] entries)
    {
        var engine = new HistoryEngine();
        foreach (var e in entries) engine.Add(e);
        var vm = new PanelViewModel(engine);
        vm.RefreshFromEngine();
        return (vm, engine);
    }

    [Fact]
    public void New_copy_at_head_keeps_existing_row_instances()
    {
        var (vm, engine) = VmWith(Text("甲"), Text("乙"), Text("丙"));
        var before = vm.Items.OfType<RowVm>().Select(r => r.Entry.Id).ToArray();
        var beforeInstances = vm.Items.OfType<RowVm>().ToArray();

        engine.Add(Text("新条目"));

        var after = vm.Items.OfType<RowVm>().ToArray();
        Assert.Equal("新条目", ((TextRowVm)after[0]).Preview); // 新行在顶
        // 旧行实例按原相对顺序保留（ReferenceEqual = 容器/DataContext 不换）
        for (var i = 0; i < beforeInstances.Length; i++)
            Assert.Same(beforeInstances[i], after[i + 1]);
        Assert.Equal(before.Length + 1, after.Length);
    }

    [Fact]
    public void Delete_keeps_neighbor_instances()
    {
        var (vm, engine) = VmWith(Text("甲", 1), Text("乙", 2), Text("丙", 3));
        var rows = vm.Items.OfType<RowVm>().ToArray();
        var midId = vm.Items.OfType<RowVm>().Skip(1).First().Entry.Id;

        engine.Remove(midId);

        var after = vm.Items.OfType<RowVm>().ToArray();
        Assert.DoesNotContain(after, r => r.Entry.Id == midId);
        Assert.Same(rows[0], after[0]);
        Assert.Same(rows[^1], after[^1]);
    }

    [Fact]
    public void ToggleFavorite_updates_flag_in_place()
    {
        var (vm, engine) = VmWith(Text("甲", 1));
        var row = vm.Items.OfType<RowVm>().First();

        engine.ToggleFavorite(row.Entry.Id);

        var rowAfter = vm.Items.OfType<RowVm>().First();
        Assert.Same(row, rowAfter); // 实例保留
        Assert.True(((TextRowVm)rowAfter).Favorite); // 标志原地刷新
    }

    [Fact]
    public void TogglePin_moves_row_and_updates_flag()
    {
        var (vm, engine) = VmWith(Text("甲", 1), Text("乙", 2));
        var bottom = vm.Items.OfType<RowVm>().Last();

        engine.TogglePin(bottom.Entry.Id);

        var rows = vm.Items.OfType<RowVm>().ToArray();
        // 移动行（跨组换位）本就是差量中段：不承诺保留实例，但内容与标志必须正确
        Assert.Equal(bottom.Entry.Id, rows[0].Entry.Id);
        Assert.True(((TextRowVm)rows[0]).Pinned);
    }

    [Fact]
    public void Tab_switch_replaces_content_completely()
    {
        var (vm, engine) = VmWith(Text("甲"), Text("乙"));
        var image = new ClipboardEntry
        {
            Id = Guid.NewGuid(),
            Kind = EntryKind.Image,
            Preview = "图像",
            ImageFile = "a".PadLeft(32, '0') + ".png",
            ImageWidth = 10,
            ImageHeight = 10,
            UtcTimestamp = DateTime.UtcNow,
        };
        engine.Add(image);

        vm.SelectedTab = PanelTab.Image;

        Assert.All(vm.Items, i => Assert.IsType<MediaTileVm>(i));
        Assert.Single(vm.Items); // 图像 Tab 只含图像条目
    }

    [Fact]
    public void Selection_restored_by_id_after_splice()
    {
        var (vm, engine) = VmWith(Text("甲", 1), Text("乙", 2));
        var target = vm.Items.OfType<RowVm>().Last();
        vm.SelectedItem = target;

        engine.Add(Text("新条目", 0)); // 头部插入，选中行下移

        Assert.Same(target, vm.SelectedItem);
    }

    [Fact]
    public void No_change_refresh_is_skipped_by_version_gate()
    {
        var (vm, engine) = VmWith(Text("甲"), Text("乙"));
        var snapshot = vm.Items.ToArray();
        vm.SelectedItem = vm.Items.OfType<RowVm>().First();

        vm.RefreshFromEngine(); // 版本未变且列表非空 → 跳过

        Assert.Same(snapshot[0], vm.Items[0]);
        Assert.Equal(snapshot.Length, vm.Items.Count);
    }

    [Fact]
    public void Duplicate_copy_replaces_head_row()
    {
        var (vm, engine) = VmWith(Text("同内容", 5), Text("乙", 10));
        var oldHead = vm.Items.OfType<RowVm>().First();

        engine.Add(Text("同内容", 0)); // 同内容：引擎头部替换（新 Id）

        var rows = vm.Items.OfType<RowVm>().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal("同内容", ((TextRowVm)rows[0]).Preview);
        Assert.NotEqual(oldHead.Entry.Id, rows[0].Entry.Id);
        // 第二行实例保留
        Assert.NotSame(oldHead, rows[1]);
    }

    [Fact]
    public void Touch_on_unchanged_position_refreshes_time_in_place()
    {
        var (vm, engine) = VmWith(Text("甲", 60 * 26)); // 昨天的时间文案与今天必然不同
        var row = vm.Items.OfType<RowVm>().First();
        var stale = row.TimeText;

        engine.Touch(row.Entry.Id); // 唯一条目：移到置顶段后第一位=位置不变，实例保留

        var rowAfter = vm.Items.OfType<RowVm>().First();
        Assert.Same(row, rowAfter); // 实例保留（容器不换）
        Assert.Equal(TimeFormat.Format(rowAfter.Entry.UtcTimestamp.ToLocalTime()), rowAfter.TimeText);
        Assert.NotEqual(stale, rowAfter.TimeText); // 时间原地刷新（Ticks 门控命中）
    }

    [Fact]
    public void Tab_counts_refresh_on_engine_change_even_during_search()
    {
        var (vm, engine) = VmWith(Text("甲"), Text("乙"));
        vm.SearchText = "甲"; // 版本未变的强刷：计数跳过重算但既有值必须保持
        var all = vm.Tabs.First(t => t.Tab == PanelTab.All);
        var before = all.CountValue;

        engine.Add(Text("丙甲")); // 引擎变更：计数门控必须放行重算

        Assert.Equal(before + 1, all.CountValue);
    }

    // ---- D1 收敛（2026-11-05）：瓦片继承 RowVm 后与行同构——旗标/来源字段齐备、消费侧单臂 ----

    private static ClipboardEntry Img(string name = "a", string? sourceApp = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = EntryKind.Image,
        Preview = "图像",
        ImageFile = name.PadLeft(32, name[0]) + ".png",
        ImageWidth = 10,
        ImageHeight = 10,
        UtcTimestamp = DateTime.UtcNow,
        SourceApp = sourceApp,
    };

    [Fact]
    public void Image_tab_tile_flags_refresh_in_place()
    {
        var image = Img("b");
        var (vm, engine) = VmWith(image);
        vm.SelectedTab = PanelTab.Image;
        var tile = Assert.IsType<MediaTileVm>(vm.Items[0]);
        Assert.False(tile.Favorite);

        engine.ToggleFavorite(tile.Entry.Id); // 图像页 Ctrl+S：瓦片旗标原地刷新（此前瓦片无任何视觉反馈）

        var tileAfter = Assert.IsType<MediaTileVm>(vm.Items[0]);
        Assert.Same(tile, tileAfter); // 实例保留（容器不换）
        Assert.True(tileAfter.Favorite);
        Assert.True(tileAfter is RowVm); // 瓦片已与行同构
    }

    [Fact]
    public void Image_row_meta_shows_source_app_not_duplicate_time()
    {
        var image = Img("c", sourceApp: "Chrome");
        var (vm, _) = VmWith(image); // 全部视图：图像行为 MediaRowVm

        var row = vm.Items.OfType<MediaRowVm>().Single();
        Assert.Equal("Chrome", row.Meta);     // 副行 = 来源应用名（与 Title 互补）
        Assert.NotEqual(row.TimeText, row.Meta); // 不再与时间同串双显
    }

    [Fact]
    public void Same_type_guard_prevents_cross_tab_alignment()
    {
        // SameKey 的类型守卫是承重墙：同 Id 的 MediaRow（全部视图）与 MediaTile（图像 Tab）
        // 若被前缀对齐复用，DataTemplate 按运行时类型解析会让图像页显示出「行」
        var image = Img("d");
        var (vm, _) = VmWith(image);
        var row = vm.Items.OfType<MediaRowVm>().Single();

        vm.SelectedTab = PanelTab.Image;

        var tile = Assert.IsType<MediaTileVm>(vm.Items[0]);
        Assert.Equal(row.Entry.Id, tile.Entry.Id); // 同一条目
        Assert.NotSame(row, tile);                 // 但 VM 不得跨 Tab 对齐复用
    }

    // ---- C10 重建路径单次 Reset（2026-11-05，拼接路径细粒度通知不动）----

    [Fact]
    public void Full_replacement_refresh_raises_single_reset_notification()
    {
        // 重建路径（首尾全不对齐=零保留实例）：搜索词变更整表换血只发 1 次 Reset，
        // 不得退回逐项 Remove/Insert 的 2N 次通知（千行级搜索每击键可感）
        var (vm, _) = VmWith(Text("甲"), Text("乙"), Text("丙"));
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.Items.CollectionChanged += (_, e) => actions.Add(e.Action);

        vm.SearchText = "zzz-no-match";

        var reset = System.Collections.Specialized.NotifyCollectionChangedAction.Reset;
        Assert.Equal([reset], actions);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void Partial_splice_keeps_granular_notifications()
    {
        // 拼接路径必须保持细粒度通知（容器保留的前提）：头部插一行 = 恰 1 次 Add，无 Reset
        var (vm, engine) = VmWith(Text("甲"), Text("乙"), Text("丙"));
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.Items.CollectionChanged += (_, e) => actions.Add(e.Action);

        engine.Add(Text("新条目"));

        var add = System.Collections.Specialized.NotifyCollectionChangedAction.Add;
        Assert.Equal([add], actions);
        Assert.Equal(4, vm.Items.OfType<RowVm>().Count());
    }

    [Fact]
    public void Engine_change_while_panel_hidden_skips_rebuild_until_next_refresh()
    {
        // 托盘驻留期（面板隐藏）剪贴板事件不整表重建（2026-11-06 性能复审⑦）；
        // 版本门控兜底：下次唤起 RefreshFromEngine 发现 Version 走远自然补刷。
        var (vm, engine) = VmWith(Text("甲"));
        vm.IsPanelVisible = false;
        engine.Add(Text("乙"));
        Assert.Single(vm.Items.OfType<RowVm>()); // 隐藏期跳过：仍是 1 行

        vm.IsPanelVisible = true; // ShowAtCursor 先开闸再刷新
        vm.RefreshFromEngine();   // 版本不等，补刷
        Assert.Equal(2, vm.Items.OfType<RowVm>().Count());
    }
}
