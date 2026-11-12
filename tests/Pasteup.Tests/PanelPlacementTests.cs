using Pasteup.App.Services;
using Xunit;

namespace Pasteup.Tests;

/// <summary>面板落位纯函数（2026-11-09 定稿）：下 → 上 → 右 → 左 → 贴边，间隙 10，
/// 前四条要求整块落在工作区内且不盖锚点。数值均为 DIP（函数本身不关心单位）。
/// 方向与实测见 docs/ideas/panel-anchor-placement.md。</summary>
public class PanelPlacementTests
{
    private static readonly PanelPlacement.Rect Work = new(0, 0, 2194, 1186);
    private static readonly PanelPlacement.Rect Panel = new(0, 0, 550, 660);

    private static PanelPlacement.Result Solve(PanelPlacement.Rect anchor) =>
        PanelPlacement.Solve(anchor, Work, Panel.W, Panel.H);

    [Fact]
    public void Gap_is_10_dip()
    {
        Assert.Equal(10.0, PanelPlacement.Gap);
    }

    [Fact]
    public void Below_aligns_top_left_at_anchor_bottom_plus_gap()
    {
        // caret：y 470、高 20 → 下置顶 = 490 + 10 = 500
        var r = Solve(new PanelPlacement.Rect(700, 470, 0, 20));
        Assert.Equal(PanelPlacement.Rule.Below, r.Rule);
        Assert.Equal(700, r.Panel.X);
        Assert.Equal(500, r.Panel.Y);
        Assert.Equal(Panel.W, r.Panel.W);
        Assert.Equal(Panel.H, r.Panel.H);
        Assert.False(r.CoversAnchor);
    }

    [Fact]
    public void Above_when_below_does_not_fit()
    {
        var r = Solve(new PanelPlacement.Rect(900, 1040, 0, 20));
        Assert.Equal(PanelPlacement.Rule.Above, r.Rule);
        Assert.Equal(900, r.Panel.X);
        Assert.Equal(1040 - 10 - 660, r.Panel.Y); // 370
        Assert.False(r.CoversAnchor);
    }

    [Fact]
    public void Right_when_neither_vertical_side_fits()
    {
        // y 600：下置 620+10+660=1290 > 1186；上置 600-10-660=-70 < 0 → 侧移右
        var r = Solve(new PanelPlacement.Rect(700, 600, 0, 20));
        Assert.Equal(PanelPlacement.Rule.Right, r.Rule);
        Assert.Equal(700 + 10, r.Panel.X);
        Assert.Equal(610 - 330, r.Panel.Y); // 竖直居中 280
        Assert.False(r.CoversAnchor);
    }

    [Fact]
    public void Left_when_right_does_not_fit()
    {
        var r = Solve(new PanelPlacement.Rect(2000, 600, 0, 20));
        Assert.Equal(PanelPlacement.Rule.Left, r.Rule);
        Assert.Equal(2000 - 10 - 550, r.Panel.X); // 1440
        Assert.Equal(280, r.Panel.Y);
        Assert.False(r.CoversAnchor);
    }

    [Fact]
    public void Fallback_when_nothing_fits_covers_anchor()
    {
        var work = new PanelPlacement.Rect(0, 0, 1024, 720);
        var r = PanelPlacement.Solve(new PanelPlacement.Rect(300, 600, 0, 20), work, 800, 660);
        Assert.Equal(PanelPlacement.Rule.Fallback, r.Rule);
        Assert.Equal(224, r.Panel.X); // 1024-800
        Assert.Equal(60, r.Panel.Y);  // 720-660
        Assert.True(r.CoversAnchor);
    }

    [Fact]
    public void Below_clamps_horizontally_at_right_edge()
    {
        var r = Solve(new PanelPlacement.Rect(2060, 300, 0, 20));
        Assert.Equal(PanelPlacement.Rule.Below, r.Rule);
        Assert.Equal(2194 - 550, r.Panel.X); // 1644
        Assert.Equal(330, r.Panel.Y);        // 300 + 20(caret 高) + 10(gap)
    }

    [Fact]
    public void Side_clamps_vertically()
    {
        var r = PanelPlacement.Solve(new PanelPlacement.Rect(700, 100, 0, 20), Work, 550, 1100);
        Assert.Equal(PanelPlacement.Rule.Right, r.Rule);
        Assert.Equal(0, r.Panel.Y); // 居中 -440 → 钳到工作区顶
    }

    [Fact]
    public void Mouse_anchor_is_zero_size_and_uses_point_as_anchor()
    {
        var r = Solve(new PanelPlacement.Rect(700, 500, 0, 0));
        Assert.Equal(PanelPlacement.Rule.Below, r.Rule);
        Assert.Equal(700, r.Panel.X);
        Assert.Equal(510, r.Panel.Y);
    }

    [Fact]
    public void Negative_origin_monitor_keeps_panel_inside_work_area()
    {
        var work = new PanelPlacement.Rect(-1234, 0, 1234, 2146);
        var r = PanelPlacement.Solve(new PanelPlacement.Rect(-600, 1000, 0, 20), work, 550, 660);
        Assert.Equal(PanelPlacement.Rule.Below, r.Rule);
        Assert.Equal(-600, r.Panel.X);
        Assert.True(r.Panel.X >= work.X && r.Panel.Right <= work.Right);
    }

    [Fact]
    public void Degenerate_panel_larger_than_work_never_produces_negative_or_overflowing_origin()
    {
        // 正常 WPF 侧由 MaxWidth/MaxHeight 挡死；纯函数仍要退化干净（2026-11-09 原型期踩过负坐标）
        var tall = PanelPlacement.Solve(new PanelPlacement.Rect(700, 600, 0, 20), Work, 550, 2000);
        Assert.True(tall.Panel.X >= Work.X);
        Assert.True(tall.Panel.Y >= Work.Y);

        var wide = PanelPlacement.Solve(new PanelPlacement.Rect(700, 300, 0, 20), Work, 3000, 660);
        Assert.Equal(0, wide.Panel.X);
    }

    [Theory]
    [InlineData(2194, 1186)]   // 本机主屏工作区
    [InlineData(1234, 2146)]   // 本机左侧竖屏工作区
    [InlineData(1024, 720)]    // 小屏
    public void Fitting_rules_stay_inside_work_area_and_never_cover_anchor(int w, int h)
    {
        var work = new PanelPlacement.Rect(0, 0, w, h);
        for (var x = 0; x <= w; x += 97)
        {
            for (var y = 0; y <= h; y += 83)
            {
                var anchor = new PanelPlacement.Rect(x, y, 0, 20);
                var r = PanelPlacement.Solve(anchor, work, 550, 660);
                Assert.True(r.Panel.X >= work.X && r.Panel.Right <= work.Right,
                    $"x 越界：anchor=({x},{y}) rule={r.Rule} panel={r.Panel}");
                Assert.True(r.Panel.Y >= work.Y && r.Panel.Bottom <= work.Bottom,
                    $"y 越界：anchor=({x},{y}) rule={r.Rule} panel={r.Panel}");
                if (r.Rule != PanelPlacement.Rule.Fallback)
                    Assert.False(r.CoversAnchor, $"非兜底规则盖住了锚点：anchor=({x},{y}) rule={r.Rule}");
            }
        }
    }
}
