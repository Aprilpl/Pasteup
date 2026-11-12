using Pasteup.App.ViewModels;
using Pasteup.Core.History;
using Pasteup.Core.Settings;
using Xunit;

namespace Pasteup.Tests;

/// <summary>唤起回顶（PanelViewModel.ResetToTop）：跳组头、平铺/网格视图、空表安全、默认开关值。</summary>
public class SummonResetTopTests
{
    private static ClipboardEntry Text(string s, int minutesAgo = 0) => new()
    {
        Id = Guid.NewGuid(),
        Kind = EntryKind.Text,
        Preview = s,
        FullText = s,
        UtcTimestamp = DateTime.UtcNow.AddMinutes(-minutesAgo),
    };

    private static ClipboardEntry Image(string name, int px, int minutesAgo = 0) => new()
    {
        Id = Guid.NewGuid(),
        Kind = EntryKind.Image,
        Preview = name,
        ImageFile = $"{Guid.NewGuid():N}.png",
        // 宽度参与 ContentKey（img:WxH:SizeBytes）：同尺寸图像会被引擎去重，须逐张错开
        ImageWidth = px,
        ImageHeight = 48,
        SizeBytes = 278,
        UtcTimestamp = DateTime.UtcNow.AddMinutes(-minutesAgo),
    };

    private static PanelViewModel VmWith(params ClipboardEntry[] entries)
    {
        var engine = new HistoryEngine();
        foreach (var e in entries) engine.Add(e);
        var vm = new PanelViewModel(engine);
        vm.RefreshFromEngine();
        return vm;
    }

    private static string? Sel(PanelViewModel vm) => vm.ActiveEntry?.Preview;

    [Fact]
    public void ResetToTop_selects_first_row_skipping_group_headers()
    {
        // 置顶条目 → 列表 [置顶组头, 钉, 今天组头, 乙, 甲]；首个非组头行 = 置顶第一行
        var engine = new HistoryEngine();
        engine.Add(Text("甲", 60));
        engine.Add(Text("乙", 50));
        var p = engine.Add(Text("钉", 5));
        engine.TogglePin(p.Id);
        var vm = new PanelViewModel(engine);
        vm.RefreshFromEngine();
        vm.MoveSelection(2); // 落到最旧「甲」，离顶部最远
        Assert.Equal("甲", Sel(vm));

        vm.ResetToTop();
        Assert.Equal("钉", Sel(vm));
        Assert.IsNotType<GroupVm>(vm.SelectedItem); // 组头永不被选中
    }

    [Fact]
    public void ResetToTop_on_flat_tab_selects_first_row()
    {
        // 收藏 Tab 平铺无组头（同置顶/今天惯例）
        var engine = new HistoryEngine();
        var a = engine.Add(Text("甲", 30));
        engine.Add(Text("乙", 20)); // 未收藏，不进 Tab
        var c = engine.Add(Text("丙", 10));
        engine.ToggleFavorite(a.Id);
        engine.ToggleFavorite(c.Id);
        var vm = new PanelViewModel(engine);
        vm.SelectedTab = PanelTab.Favorite; // OnSelectedTabChanged → force 刷新
        vm.MoveSelection(1); // 选中最旧收藏「甲」
        Assert.Equal("甲", Sel(vm));

        vm.ResetToTop();
        Assert.Equal("丙", Sel(vm)); // 收藏视图内最新（引擎序最前）
    }

    [Fact]
    public void ResetToTop_on_image_tab_selects_first_tile()
    {
        var vm = VmWith(Image("图一", 64, 30), Image("图二", 65, 20), Image("图三", 66, 10));
        vm.SelectedTab = PanelTab.Image; // 单列全宽瓦片
        vm.MoveSelection(2); // 选中最旧瓦片「图一」
        Assert.Equal("图一", Sel(vm));

        vm.ResetToTop();
        var tile = Assert.IsType<MediaTileVm>(vm.SelectedItem);
        Assert.Equal("图三", tile.Entry.Preview);
    }

    [Fact]
    public void ResetToTop_on_empty_list_is_noop()
    {
        var vm = VmWith();
        vm.ResetToTop();
        Assert.Null(vm.SelectedItem);
    }

    [Fact]
    public void ResetToTop_overrides_prior_selection_without_refresh()
    {
        // 门控外调用契约：引擎未变、refresh 跳过时，重置仍按当前 Items 生效
        var vm = VmWith(Text("甲"), Text("乙"), Text("丙"));
        vm.MoveSelection(2); // 「甲」
        vm.ResetToTop();     // 不经 RefreshFromEngine
        Assert.Equal("丙", Sel(vm));
    }

    [Fact]
    public void SummonResetToTop_defaults_on()
        => Assert.True(new AppSettings().SummonResetToTop);
}
