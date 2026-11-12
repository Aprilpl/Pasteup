using Pasteup.App.Services;
using Xunit;

namespace Pasteup.Tests;

/// <summary>空闲修剪状态机 + 窗口枚举判定（2026-11-03 内存优化：窗口全隐藏后修剪工作集）。</summary>
public class WorkingSetTrimmerTests
{
    [Fact]
    public void 全隐藏后首tick修剪一次_后续tick不再修剪()
    {
        var s = new IdleTrimState();
        Assert.True(s.OnTick(anyVisibleWindow: false));   // 隐藏后的第一个 tick：修剪
        Assert.False(s.OnTick(anyVisibleWindow: false));  // 同一轮空闲内：只此一次
        Assert.False(s.OnTick(anyVisibleWindow: false));
    }

    [Fact]
    public void 可见期间不修剪_重新隐藏后再次修剪()
    {
        var s = new IdleTrimState();
        Assert.False(s.OnTick(anyVisibleWindow: true));   // 面板开着：不修剪
        Assert.False(s.OnTick(anyVisibleWindow: true));
        Assert.True(s.OnTick(anyVisibleWindow: false));   // 隐藏：修剪
        Assert.False(s.OnTick(anyVisibleWindow: false));
        Assert.False(s.OnTick(anyVisibleWindow: true));   // 再显：重新武装
        Assert.True(s.OnTick(anyVisibleWindow: false));   // 再隐：又修剪
    }

    [Fact]
    public void 快速开关面板_交替tick不产生连续修剪()
    {
        var s = new IdleTrimState();
        // 开-关-开-关：每个隐藏窗口期恰一次修剪，无 churn
        Assert.False(s.OnTick(true));
        Assert.True(s.OnTick(false));
        Assert.False(s.OnTick(true));
        Assert.True(s.OnTick(false));
    }

    [Fact]
    public void 测试宿主无WPF顶层窗口_枚举判定为无可见窗口()
    {
        // xunit 宿主是控制台进程，没有 HwndWrapper 类窗口；此用例同时锁定
        // 「托盘图标的隐藏 Win32 窗口不算可见窗口」的口径（若有泄漏的可见窗口会误报）
        Assert.False(WorkingSetTrimmer.AnyVisibleOwnWindow());
    }
}
