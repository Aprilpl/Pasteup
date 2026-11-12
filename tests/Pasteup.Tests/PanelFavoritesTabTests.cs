using Pasteup.App.ViewModels;
using Pasteup.Core.History;
using Xunit;

namespace Pasteup.Tests;

/// <summary>
/// 收藏 Tab（PanelTab.Favorite）：8 等分分类行的第 2 格（全部与置顶之间）、
/// 过滤只看 Favorite、整表平铺不加组头、搜索在收藏集内生效、计数随切换实时刷新。
/// 引擎侧收藏语义（豁免/清空/ToggleFavorite）在 HistoryTests；行徽章位在 PanelSelectionTests。
/// </summary>
public class PanelFavoritesTabTests
{
    private static ClipboardEntry Text(string s, int minutesAgo = 0) => new()
    {
        Id = Guid.NewGuid(),
        Kind = EntryKind.Text,
        Preview = s,
        FullText = s,
        UtcTimestamp = DateTime.UtcNow.AddMinutes(-minutesAgo),
    };

    private static (HistoryEngine Engine, PanelViewModel Vm) VmOn(params ClipboardEntry[] entries)
    {
        var engine = new HistoryEngine();
        foreach (var e in entries) engine.Add(e);
        var vm = new PanelViewModel(engine);
        vm.RefreshFromEngine();
        return (engine, vm);
    }

    [Fact]
    public void Tabs_are_eight_and_favorite_sits_second()
    {
        var (_, vm) = VmOn(Text("甲"));
        Assert.Equal(8, vm.Tabs.Count);
        var fav = vm.Tabs[1];
        Assert.Equal(PanelTab.Favorite, fav.Tab);
        Assert.Equal("收藏", fav.Label);
        Assert.False(fav.Filled); // 分类标签用 regular 星（操作入口），Filled 是状态标语义
    }

    [Fact]
    public void Favorite_tab_count_tracks_flag()
    {
        var engine = new HistoryEngine();
        var a = engine.Add(Text("甲"));
        engine.Add(Text("乙"));
        engine.ToggleFavorite(a.Id);
        var vm = new PanelViewModel(engine);
        Assert.Equal(1, vm.Tabs[1].CountValue);
        engine.ToggleFavorite(a.Id);
        vm.RefreshFromEngine();
        Assert.Equal(0, vm.Tabs[1].CountValue);
    }

    [Fact]
    public void Favorite_tab_filters_flat_with_pinned_prefix()
    {
        var engine = new HistoryEngine();
        engine.Add(Text("甲", 60));
        engine.Add(Text("乙", 50));
        engine.Add(Text("丙", 40));
        engine.Add(Text("丁", 30));
        engine.ToggleFavorite(engine.Snapshot()[0].Id); // 丁（引擎最新在前）
        engine.ToggleFavorite(engine.Snapshot()[3].Id); // 甲
        var pin = engine.Add(Text("钉", 5));
        engine.TogglePin(pin.Id);
        engine.ToggleFavorite(pin.Id); // 置顶+收藏并存
        var vm = new PanelViewModel(engine);
        vm.RefreshFromEngine();

        vm.SelectTabCommand.Execute(PanelTab.Favorite);
        // 平铺：无组头；引擎序 = 置顶收藏（钉）在最上，其余按最近使用/复制序
        Assert.Empty(vm.Items.OfType<GroupVm>());
        Assert.Equal(["钉", "丁", "甲"],
            vm.Items.OfType<TextRowVm>().Select(r => r.Preview).ToArray());
        Assert.Equal("3 条", vm.CountText);
        // 双旗并存：钉行同时挂置顶与收藏徽章位
        var ding = Assert.IsType<TextRowVm>(vm.Items.OfType<TextRowVm>().First(r => r.Preview == "钉"));
        Assert.True(ding.Pinned);
        Assert.True(ding.Favorite);
    }

    [Fact]
    public void Favorite_tab_search_scopes_inside_favorites()
    {
        var engine = new HistoryEngine();
        engine.Add(Text("甲乙", 30));
        engine.Add(Text("丙", 20));
        var c = engine.Add(Text("乙丁", 10));
        engine.ToggleFavorite(c.Id);
        var vm = new PanelViewModel(engine);
        vm.RefreshFromEngine();

        vm.SelectTabCommand.Execute(PanelTab.Favorite);
        vm.SearchText = "乙"; // 只命中收藏集内的「乙丁」，不命中未收藏的「甲乙」
        Assert.Equal(["乙丁"], vm.Items.OfType<TextRowVm>().Select(r => r.Preview).ToArray());

        vm.SearchText = "丙"; // 未收藏条目在收藏 Tab 搜不到
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void Unfavorite_in_favorite_tab_updates_count_and_items()
    {
        var engine = new HistoryEngine();
        engine.Add(Text("甲", 20));
        var b = engine.Add(Text("乙", 10));
        engine.ToggleFavorite(b.Id);
        var vm = new PanelViewModel(engine);
        vm.RefreshFromEngine();

        vm.SelectTabCommand.Execute(PanelTab.Favorite);
        Assert.Single(vm.Items);
        vm.MoveSelection(1); // 选中「乙」
        vm.ToggleFavoriteCommand.Execute(null); // 收藏视图里取消收藏 → 行移除、计数归零
        Assert.Empty(vm.Items.OfType<TextRowVm>());
        Assert.Equal(0, vm.Tabs[1].CountValue);
        Assert.Equal("0 条", vm.CountText);
    }
}
