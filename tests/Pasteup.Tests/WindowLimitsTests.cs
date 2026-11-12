using Pasteup.App.Services;
using Pasteup.Core.Capture;
using Xunit;

namespace Pasteup.Tests;

/// <summary>窗口尺寸上限：宽 ≤ 所在屏工作区 50%、高 ≤ 工作区 100%（DIP）；
/// 与旧约束（宽不限、高封顶 680/720）正好对调。</summary>
public class WindowLimitsTests
{
    // 1920×1040 工作区 @96dpi → 宽帽 960、高帽 1040
    [Fact]
    public void Caps_half_width_full_height_at_96dpi()
    {
        var m = new MonitorRect("d", 0, 0, 1920, 1080, true, 96, 0, 0, 1920, 1040);
        Assert.Equal((960.0, 1040.0), WindowLimits.Caps(m, 480, 380));
    }

    // 144dpi：工作区 2880×1560 物理 = 1920×1040 DIP → 同 96dpi 结果
    [Fact]
    public void Caps_convert_dpi_to_dip()
    {
        var m = new MonitorRect("d", 0, 0, 2880, 1620, true, 144, 0, 0, 2880, 1560);
        Assert.Equal((960.0, 1040.0), WindowLimits.Caps(m, 480, 380));
    }

    // 小屏 50% 工作区窄于 Min：取 Min 防倒挂（WPF 要求 Min ≤ Max），高不封顶仍按工作区
    [Fact]
    public void Caps_never_below_minimums()
    {
        var m = new MonitorRect("d", 0, 0, 1366, 768, true, 96, 0, 0, 1366, 728);
        Assert.Equal((720.0, 728.0), WindowLimits.Caps(m, 720, 480));
    }

    // 工作区缺省（枚举异常回退 0）：退回全屏矩形
    [Fact]
    public void Caps_fall_back_to_full_rect_when_work_missing()
    {
        var m = new MonitorRect("d", 0, 0, 1920, 1080, true, 96);
        Assert.Equal((960.0, 1080.0), WindowLimits.Caps(m, 480, 380));
    }
}
