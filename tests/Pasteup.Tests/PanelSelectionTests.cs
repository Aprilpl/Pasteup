using Pasteup.App.ViewModels;
using Pasteup.Core.History;
using Xunit;

namespace Pasteup.Tests;

/// <summary>面板 ↑↓ 导航（PanelViewModel.MoveSelection）：钳制不循环、跳过组头、空表安全。</summary>
public class PanelSelectionTests
{
    private static ClipboardEntry Text(string s, int minutesAgo = 0) => new()
    {
        Id = Guid.NewGuid(),
        Kind = EntryKind.Text,
        Preview = s,
        FullText = s,
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
    public void Down_moves_next_then_stops_at_end()
    {
        var vm = VmWith(Text("甲"), Text("乙"), Text("丙"));
        Assert.Equal("丙", Sel(vm)); // 初始选中引擎最前一条
        vm.MoveSelection(1);
        Assert.Equal("乙", Sel(vm));
        vm.MoveSelection(1);
        Assert.Equal("甲", Sel(vm));
        vm.MoveSelection(1); // 尾部停住
        Assert.Equal("甲", Sel(vm));
    }

    [Fact]
    public void Up_stops_at_first()
    {
        var vm = VmWith(Text("甲"), Text("乙"));
        Assert.Equal("乙", Sel(vm));
        vm.MoveSelection(-1); // 首部停住
        Assert.Equal("乙", Sel(vm));
    }

    [Fact]
    public void No_selection_down_lands_first_up_lands_last()
    {
        var vm = VmWith(Text("甲"), Text("乙"), Text("丙"));
        vm.SelectedItem = null;
        vm.MoveSelection(-1);
        Assert.Equal("甲", Sel(vm)); // ↑ 落最后一条（列表最旧端）

        vm.SelectedItem = null;
        vm.MoveSelection(1);
        Assert.Equal("丙", Sel(vm)); // ↓ 落第一条
    }

    [Fact]
    public void Skips_group_headers_without_cycling()
    {
        // 置顶条目最后操作 → 列表 [钉* 乙 甲]，分组产生两个组头夹在行间
        var engine = new HistoryEngine();
        engine.Add(Text("甲", 60));
        engine.Add(Text("乙", 50));
        var p = engine.Add(Text("钉", 5));
        engine.TogglePin(p.Id);
        var vm = new PanelViewModel(engine);
        vm.RefreshFromEngine();

        Assert.Equal("钉", Sel(vm));
        vm.MoveSelection(1); // 跳过「今天」组头
        Assert.Equal("乙", Sel(vm));
        vm.MoveSelection(1);
        Assert.Equal("甲", Sel(vm));
        vm.MoveSelection(1);
        Assert.Equal("甲", Sel(vm)); // 尾部停住
        vm.MoveSelection(-1);
        Assert.Equal("乙", Sel(vm));
        vm.MoveSelection(-1);
        Assert.Equal("钉", Sel(vm));
        vm.MoveSelection(-1); // 首部停住（上方组头不参与）
        Assert.Equal("钉", Sel(vm));
    }

    [Fact]
    public void Empty_list_is_noop()
    {
        var vm = VmWith();
        vm.MoveSelection(1);
        vm.MoveSelection(-1);
        Assert.Null(vm.SelectedItem);
    }

    // ---- 收藏命令与行徽章位（引擎增量经 Changed → RefreshFromEngine 全链路） ----

    [Fact]
    public void ToggleFavoriteCommand_flips_entry_and_marks_row()
    {
        var vm = VmWith(Text("甲"), Text("乙"));
        vm.ToggleFavoriteCommand.Execute(null); // 选中「乙」（引擎最前）
        Assert.True(vm.ActiveEntry!.Favorite);
        var row = Assert.IsType<TextRowVm>(vm.SelectedItem);
        Assert.True(row.Favorite); // RefreshFromEngine 重建后徽章位带出
        vm.ToggleFavoriteCommand.Execute(null);
        Assert.False(vm.ActiveEntry!.Favorite);
    }

    [Fact]
    public void Favorite_does_not_move_row_position()
    {
        var engine = new HistoryEngine();
        engine.Add(Text("甲", 30));
        engine.Add(Text("乙", 20));
        engine.Add(Text("丙", 10));
        var vm = new PanelViewModel(engine);
        vm.RefreshFromEngine();
        vm.MoveSelection(2); // 选中「甲」（最旧）
        vm.ToggleFavoriteCommand.Execute(null);
        // 收藏只打标记：行序不变（置顶才会移组）
        Assert.Equal(["丙", "乙", "甲"],
            vm.Items.OfType<TextRowVm>().Select(r => r.Preview).ToArray());
        Assert.True(vm.Items.OfType<TextRowVm>().Last(r => r.Preview == "甲").Favorite);
    }
}
