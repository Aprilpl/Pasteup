using Pasteup.App.Services;
using Xunit;

namespace Pasteup.Tests;

/// <summary>窗口 DIP 尺寸记忆（2026-11-04）：Restore 的合法域回退与 Record 的差值门控。</summary>
public class WindowSizeMemoryTests
{
    [Theory]
    [InlineData(0)]      // 未记忆
    [InlineData(-5)]     // 损坏
    [InlineData(379.9)]  // 低于窗口 MinHeight(380)
    [InlineData(20001)]  // 超出合理上限
    public void Restore_out_of_range_falls_back(double stored)
        => Assert.Equal(660, WindowSizeMemory.Restore(stored, 660, 380));

    [Fact]
    public void Restore_valid_returns_stored()
        => Assert.Equal(940, WindowSizeMemory.Restore(940, 660, 380));

    [Fact]
    public void Restore_nan_falls_back()
        => Assert.Equal(660, WindowSizeMemory.Restore(double.NaN, 660, 380));

    [Fact]
    public void Record_rounds_and_skips_unchanged()
    {
        // 首次记录（已存 0/0）：Round 到整 DIP
        Assert.Equal((800.0, 700.0), WindowSizeMemory.Record(800.4, 699.6, 0, 0));
        // 还原触发的 SizeChanged：与已存差 <1 → null（不写设置不触发落盘）
        Assert.Null(WindowSizeMemory.Record(800.2, 700.1, 800, 700));
        // 用户拖动：宽变 2 DIP → 记录新值
        Assert.Equal((802.0, 700.0), WindowSizeMemory.Record(802.0, 700.4, 800, 700));
    }

    [Fact]
    public void Record_unstable_values_returns_null()
    {
        Assert.Null(WindowSizeMemory.Record(0, 700, 0, 0));      // 布局未稳（宽 0）
        Assert.Null(WindowSizeMemory.Record(800, double.NaN, 0, 0)); // 布局异常值
    }
}
