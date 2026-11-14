using Pasteup.App.Services;
using Xunit;

namespace Pasteup.Tests;

/// <summary>托盘「贴图列表」子菜单文案契约：贴图是瞬时窗口、每次展开实时枚举，
/// 故标题须带当前数量、0 张给禁用提示、每行序号 + 原始物理尺寸、末尾「全部关闭」。</summary>
public class PinListTextTests
{
    [Fact]
    public void Header_without_pins_is_plain_title()
    {
        Assert.Equal("贴图列表", PinListText.Header(0));
        // 防御：数量异常（负值）也不允许拼出「（-1）」这类标题
        Assert.Equal("贴图列表", PinListText.Header(-1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Header_with_pins_appends_count(int count)
        => Assert.Equal($"贴图列表（{count}）", PinListText.Header(count));

    [Theory]
    [InlineData(1, 1024, 768, "贴图 1 · 1024×768")]
    [InlineData(2, 200, 100, "贴图 2 · 200×100")]
    [InlineData(12, 3840, 2076, "贴图 12 · 3840×2076")]
    public void Item_label_shows_order_and_physical_size(int index, int w, int h, string expected)
        => Assert.Equal(expected, PinListText.ItemLabel(index, w, h));

    [Fact]
    public void Empty_state_and_close_all_wording_are_stable()
    {
        Assert.Equal("暂无贴图（Alt+S 框选截图可生成）", PinListText.EmptyHint);
        Assert.Equal("全部关闭", PinListText.CloseAll);
        // 空状态提示与「全部关闭」不能是同一句（菜单组装方按文案区分两行用途）
        Assert.NotEqual(PinListText.EmptyHint, PinListText.CloseAll);
    }
}
