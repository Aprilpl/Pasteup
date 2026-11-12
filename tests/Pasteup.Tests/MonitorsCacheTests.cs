using Pasteup.Core.Capture;
using Xunit;

namespace Pasteup.Tests;

/// <summary>显示器枚举缓存（2026-11-05 C11）：拖动期每次 LocationChanged 都查询，
/// 缓存把全套 P/Invoke 压到「每代一次」。契约=同代返回同一实例（调用方只读），
/// Invalidate 后换新实例。</summary>
public class MonitorsCacheTests
{
    [Fact]
    public void All_shares_one_instance_until_invalidated()
    {
        var a = Monitors.All();
        Assert.Same(a, Monitors.All());
    }

    [Fact]
    public void Invalidate_forces_reenumeration()
    {
        var a = Monitors.All();
        Monitors.Invalidate();
        var b = Monitors.All();
        Assert.NotSame(a, b);
        // 新一代内容来自同一台机器的枚举：设备名集合一致（顺序可能变，不比对序）
        Assert.Equal(
            a.Select(m => m.DeviceName).OrderBy(n => n, StringComparer.Ordinal),
            b.Select(m => m.DeviceName).OrderBy(n => n, StringComparer.Ordinal));
    }
}
