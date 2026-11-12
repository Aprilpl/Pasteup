using Pasteup.App.Services;
using Xunit;

namespace Pasteup.Tests;

/// <summary>清空历史确认对话框：说明文案分段（数字段等宽）与选项可见性纯函数。
/// keep = 置顶 ∪ 收藏（默认清空保留的数量）；favorite &gt; 0 时文案提及「收藏」。</summary>
public class ClearConfirmTests
{
    private static string Plain(int total, int keep, int favorite, bool clearPinned)
        => string.Concat(ClearConfirmText.Segments(total, keep, favorite, clearPinned).Select(s => s.Text));

    // ---- 不勾（默认）：清除历史但保留置顶/收藏 ----

    [Fact]
    public void Unchecked_keeps_pinned_and_shows_both_counts()
    {
        var text = Plain(772, 3, favorite: 0, clearPinned: false);
        Assert.Equal("将删除 769 个项目，保留 3 个置顶项目；此操作不可撤销。", text);
    }

    [Fact]
    public void Unchecked_marks_numbers_as_mono()
    {
        var segs = ClearConfirmText.Segments(772, 3, favorite: 0, clearPinned: false);
        Assert.Equal(new[] { false, true, false, true, false }, segs.Select(s => s.IsMono));
        Assert.Equal("769", segs[1].Text);
        Assert.Equal("3", segs[3].Text);
    }

    // ---- 勾选：连置顶一起全清 ----

    [Fact]
    public void Checked_clears_everything_including_pinned()
    {
        var text = Plain(772, 3, favorite: 0, clearPinned: true);
        Assert.Equal("将删除全部 772 个项目（含置顶 3 个）；此操作不可撤销。", text);
    }

    [Fact]
    public void Checked_marks_numbers_as_mono()
    {
        var segs = ClearConfirmText.Segments(772, 3, favorite: 0, clearPinned: true);
        Assert.Equal(new[] { false, true, false, true, false }, segs.Select(s => s.IsMono));
        Assert.Equal("772", segs[1].Text);
        Assert.Equal("3", segs[3].Text);
    }

    // ---- 收藏在场：口径 = 置顶 ∪ 收藏，文案提及「置顶/收藏」 ----

    [Fact]
    public void Favorite_widens_scope_wording_and_keep_count()
    {
        // 772 条中 3 置顶、另有 5 仅收藏 → 保留 8
        var text = Plain(772, keep: 8, favorite: 5, clearPinned: false);
        Assert.Equal("将删除 764 个项目，保留 8 个置顶/收藏项目；此操作不可撤销。", text);
    }

    [Fact]
    public void Favorite_checked_clears_all_with_scope_wording()
    {
        var text = Plain(772, keep: 8, favorite: 5, clearPinned: true);
        Assert.Equal("将删除全部 772 个项目（含置顶/收藏 8 个）；此操作不可撤销。", text);
    }

    [Fact]
    public void Favorite_only_without_pinned_shows_checkbox_and_keeps_them()
    {
        // 无置顶、仅 2 收藏：默认清空保留收藏，复选框照常出现
        var text = Plain(500, keep: 2, favorite: 2, clearPinned: false);
        Assert.Equal("将删除 498 个项目，保留 2 个置顶/收藏项目；此操作不可撤销。", text);
        Assert.True(ClearConfirmText.ShouldShowCheckbox(2));
    }

    // ---- 无置顶无收藏：复选框隐藏，文案只有全清一句（勾选态被忽略） ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_keep_hides_option_and_ignores_flag(bool clearPinned)
    {
        var segs = ClearConfirmText.Segments(500, 0, 0, clearPinned);
        Assert.Equal("将删除全部 500 个项目；此操作不可撤销。", string.Concat(segs.Select(s => s.Text)));
        Assert.Equal(new[] { false, true, false }, segs.Select(s => s.IsMono));
        Assert.False(ClearConfirmText.ShouldShowCheckbox(0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Checkbox_visible_when_any_keep(int keep)
        => Assert.True(ClearConfirmText.ShouldShowCheckbox(keep));
}
