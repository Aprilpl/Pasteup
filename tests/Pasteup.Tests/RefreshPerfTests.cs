using Pasteup.App.ViewModels;
using Pasteup.Core.History;
using Xunit;

namespace Pasteup.Tests;

/// <summary>
/// 第 3 批 P1-2 刷新性能与契约（2026-11-11）：
/// ① RefreshFromEngine 先按「组头标签 / 行 Id+具体类型」键序列求公共前后缀，只 materialize 变化中段
///    ——前后缀 VM 实例必须整段保留（容器不换），组头计数原地刷新；无保留段仍走单次 Reset；
/// ② HistoryEngine.Counts 一次加锁单遍出的 8 个计数与旧 CountsByKind/Query 口径一致；
/// ③ Enter 关闸：IsPanelVisible=false 先于 Touch，引擎变更不触发重建（下次唤起版本门控补刷）。
/// </summary>
public class RefreshPerfTests
{
    private static ClipboardEntry Text(string s) => new()
    {
        Id = Guid.NewGuid(),
        Kind = EntryKind.Text,
        Preview = s,
        FullText = s,
        UtcTimestamp = DateTime.UtcNow,
    };

    /// <summary>按**本地**时刻构造条目（分组按本地日期判定，避免测试在凌晨附近因「今天」漂移）。</summary>
    private static ClipboardEntry AtLocal(string s, DateTime localTime)
    {
        var e = Text(s);
        e.UtcTimestamp = localTime.ToUniversalTime();
        return e;
    }

    private static (PanelViewModel Vm, HistoryEngine Engine) VmWith(params ClipboardEntry[] entries)
    {
        var engine = new HistoryEngine();
        foreach (var e in entries) engine.Add(e);
        var vm = new PanelViewModel(engine);
        vm.RefreshFromEngine();
        return (vm, engine);
    }

    // ===== ② Tab 计数单遍 =====

    [Fact]
    public void Counts_single_pass_matches_legacy_queries_and_counts_by_kind()
    {
        var engine = new HistoryEngine();
        var textA = Text("文本一");
        var textB = Text("文本二");
        engine.Add(textA);
        engine.Add(textB);
        engine.ToggleFavorite(textA.Id);
        var img = ClipboardEntry.FromImage($"{Guid.NewGuid():N}.png", 10, 20, 300, "A");
        engine.Add(img);
        engine.TogglePin(img.Id);
        engine.Add(ClipboardEntry.FromText("https://example.dev/x", "B")); // Kind=Link
        engine.Add(ClipboardEntry.FromFiles([@"C:\a\b.txt"], "C"));         // Kind=File
        engine.Add(AtLocal("十天前", DateTime.Today.AddDays(-10).AddHours(9)));

        var counts = engine.Counts(DateTime.Today);

        // 与旧实现（Count / Query(筛选) / CountsByKind）逐项一致
        Assert.Equal(engine.Count, counts.Total);
        Assert.Equal(engine.Query(filter: static e => e.Favorite).Count, counts.Favorite);
        Assert.Equal(engine.Query(filter: static e => e.Pinned).Count, counts.Pinned);
        Assert.Equal(engine.Query(filter: static e => e.UtcTimestamp.ToLocalTime() >= DateTime.Today).Count,
            counts.Today);
        var kinds = engine.CountsByKind();
        Assert.Equal(kinds[EntryKind.Text], counts.Text);
        Assert.Equal(kinds[EntryKind.Image], counts.Image);
        Assert.Equal(kinds[EntryKind.Link], counts.Link);
        Assert.Equal(kinds[EntryKind.File], counts.File);

        // 具体数值（6 条：今天 5 条 + 十天前 1 条）
        Assert.Equal(6, counts.Total);
        Assert.Equal(1, counts.Favorite);
        Assert.Equal(1, counts.Pinned);
        Assert.Equal(5, counts.Today);
        Assert.Equal((3, 1, 1, 1), (counts.Text, counts.Image, counts.Link, counts.File));
    }

    [Fact]
    public void Counts_today_boundary_uses_supplied_local_midnight()
    {
        var engine = new HistoryEngine();
        var midnight = DateTime.Today; // 本地今天 00:00（Counts 的 todayLocalStart 契约）
        engine.Add(AtLocal("正点", midnight));                    // == 起点 → 今天
        engine.Add(AtLocal("午夜前一秒", midnight.AddSeconds(-1))); // < 起点 → 昨天

        var counts = engine.Counts(midnight);

        Assert.Equal(2, counts.Total);
        Assert.Equal(1, counts.Today);
    }

    [Fact]
    public void Panel_tabs_read_cached_counts_and_still_refresh_per_engine_version()
    {
        var (vm, engine) = VmWith(Text("甲"), Text("乙"));
        var all = vm.Tabs.First(t => t.Tab == PanelTab.All);
        var fav = vm.Tabs.First(t => t.Tab == PanelTab.Favorite);
        Assert.Equal(2, all.CountValue);
        Assert.Equal(0, fav.CountValue);

        engine.ToggleFavorite(engine.Snapshot()[0].Id); // 版本变化 → 一次 Counts 全部 Tab 刷新

        Assert.Equal(1, fav.CountValue);
        Assert.Equal(2, all.CountValue);
    }

    // ===== ① 只 materialize 变化中段、前后缀实例保留 =====

    [Fact]
    public void Middle_delete_materializes_only_changed_range_and_keeps_surrounding_instances()
    {
        // 加入顺序=最旧在前（Add 插头部），行序：今天组头+4 行、昨天组头+2 行
        var engine = new HistoryEngine();
        engine.Add(AtLocal("昨2", DateTime.Today.AddDays(-1).AddHours(8)));
        engine.Add(AtLocal("昨1", DateTime.Today.AddDays(-1).AddHours(9)));
        engine.Add(AtLocal("今1", DateTime.Today.AddHours(8)));
        engine.Add(AtLocal("今2", DateTime.Today.AddHours(9)));
        engine.Add(AtLocal("今3", DateTime.Today.AddHours(10)));
        engine.Add(AtLocal("今4", DateTime.Today.AddHours(11)));
        var vm = new PanelViewModel(engine);
        vm.RefreshFromEngine();

        var before = vm.Items.ToArray();
        Assert.Equal(8, before.Length); // 今天组头 + 4 行 + 昨天组头 + 2 行
        var todayHeader = Assert.IsType<GroupVm>(before[0]);
        var yesterdayHeader = Assert.IsType<GroupVm>(before[5]);
        Assert.Equal("4", todayHeader.CountText);
        Assert.Equal("2", yesterdayHeader.CountText);

        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.Items.CollectionChanged += (_, e) => actions.Add(e.Action);

        // 删掉今天组中段一行：变化点两侧（含两个组头）都应保留原实例，且不得退化为 Reset
        engine.Remove(((RowVm)before[3]).Entry.Id);

        var after = vm.Items.ToArray();
        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Remove], actions);
        Assert.Equal(7, after.Length);
        // 组头实例保留 + 计数原地刷新（今天的 4→3，昨天的 2 不变）
        Assert.Same(todayHeader, after[0]);
        Assert.Equal("3", todayHeader.CountText);
        Assert.Same(yesterdayHeader, after[4]);
        Assert.Equal("2", yesterdayHeader.CountText);
        // 前后缀行实例整段保留
        Assert.Same(before[1], after[1]);
        Assert.Same(before[2], after[2]);
        Assert.Same(before[4], after[3]);
        Assert.Same(before[5], after[4]);
        Assert.Same(before[6], after[5]);
        Assert.Same(before[7], after[6]);
    }

    [Fact]
    public void Flag_only_change_keeps_all_instances_without_collection_notifications()
    {
        var (vm, engine) = VmWith(Text("甲"), Text("乙"), Text("丙")); // 行序：丙 乙 甲
        var before = vm.Items.ToArray();
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.Items.CollectionChanged += (_, e) => actions.Add(e.Action);
        var target = Assert.IsType<TextRowVm>(before[2]); // 乙

        engine.ToggleFavorite(target.Entry.Id); // 键序列不变：零 materialize、零集合通知

        Assert.Empty(actions);
        Assert.Equal(before.Length, vm.Items.Count);
        for (var i = 0; i < before.Length; i++) Assert.Same(before[i], vm.Items[i]);
        Assert.True(target.Favorite); // 旗标原地刷新
    }

    [Fact]
    public void New_head_row_keeps_existing_suffix_instances_and_group_header()
    {
        var (vm, engine) = VmWith(Text("甲"), Text("乙"), Text("丙")); // 行序：丙 乙 甲
        var before = vm.Items.ToArray();
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.Items.CollectionChanged += (_, e) => actions.Add(e.Action);

        engine.Add(Text("新条目")); // 头部插入：前缀=组头，后缀=三条旧行

        var after = vm.Items.ToArray();
        // 恰 1 次 Add（细粒度通知=容器保留的前提；若退化为 Reset 契约即破）
        Assert.Equal([System.Collections.Specialized.NotifyCollectionChangedAction.Add], actions);
        Assert.Equal(before.Length + 1, after.Length);
        Assert.Same(before[0], after[0]); // 组头实例保留
        for (var i = 0; i < before.Length - 1; i++) Assert.Same(before[i + 1], after[i + 2]);
        Assert.Equal("4", ((GroupVm)after[0]).CountText); // 组头计数原地刷新
    }

    // ===== ③ Enter 关闸 =====

    [Fact]
    public void Enter_gate_before_touch_skips_rebuild_until_next_refresh()
    {
        var (vm, engine) = VmWith(Text("甲"), Text("乙"), Text("丙"));
        var before = vm.Items.ToArray();
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        vm.Items.CollectionChanged += (_, e) => actions.Add(e.Action);
        var oldest = before.OfType<RowVm>().Last(); // 甲

        // ActivateSelected 的顺序：先 ViewModel.IsPanelVisible=false，再 History.Touch
        vm.IsPanelVisible = false;
        engine.Touch(oldest.Entry.Id);

        // 可见期重建（Touch 会移行）已被关闸拦下：列表原样、零通知
        Assert.Empty(actions);
        Assert.Equal(before.Length, vm.Items.Count);
        for (var i = 0; i < before.Length; i++) Assert.Same(before[i], vm.Items[i]);

        // 下次唤起：先开闸再刷新，版本门控兜底补刷出新顺序（MRU 到第一条）
        vm.IsPanelVisible = true;
        vm.RefreshFromEngine();
        Assert.Equal(oldest.Entry.Id, vm.Items.OfType<RowVm>().First().Entry.Id);
    }

    // ===== ④ 粘贴 pid 固定复核（防御性边界：零句柄/零 pid 一律不放行） =====

    [Fact]
    public void Pid_guard_rejects_zero_handle_or_pid()
    {
        Assert.False(Pasteup.App.Services.Win32Input.ForegroundBelongsTo(IntPtr.Zero));
        Assert.False(Pasteup.App.Services.Win32Input.ForegroundBelongsTo(IntPtr.Zero, 1));
        Assert.False(Pasteup.App.Services.Win32Input.ForegroundBelongsTo(new IntPtr(1), 0));
    }
}
