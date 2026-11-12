using Pasteup.Core.Capture;
using Xunit;

namespace Pasteup.Tests;

public class CaptureMathTests
{
    [Theory]
    [InlineData(100, 100, 200, 200, 100, 100, 100, 100)]       // 正常拖拽
    [InlineData(200, 200, 100, 100, 100, 100, 100, 100)]       // 反向拖拽
    [InlineData(50, 50, 50, 50, 50, 50, 0, 0)]                  // 原点不动
    public void Normalize_fixes_direction(int x1, int y1, int x2, int y2, int ex, int ey, int ew, int eh)
    {
        var r = CaptureMath.Normalize(x1, y1, x2, y2);
        Assert.Equal(new CaptureRect(ex, ey, ew, eh), r);
    }

    [Theory]
    [InlineData(100, 100, 103, 200)]   // 宽 3 < 4 → 空
    [InlineData(100, 100, 104, 200)]   // 宽 4 → 恰好达阈值，不空
    [InlineData(100, 100, 300, 108)]   // 高 8 ≥ 4 → 不空
    [InlineData(100, 100, 103, 103)]   // 双向过小 → 空
    public void NormalizeDrag_threshold(int x1, int y1, int x2, int y2)
    {
        var r = CaptureMath.NormalizeDrag(x1, y1, x2, y2);
        var shouldBeEmpty = Math.Abs(x2 - x1) < 4 || Math.Abs(y2 - y1) < 4;
        Assert.Equal(shouldBeEmpty, r.IsEmpty);
    }

    [Fact]
    public void NormalizeDrag_of_tiny_jitter_is_empty()
        => Assert.True(CaptureMath.NormalizeDrag(10, 10, 12, 300).IsEmpty);

    [Theory]
    [InlineData(0, 0, 10, 10, 5, 5, 10, 10, 5, 5, 5, 5)]         // a 左上角在 b 内
    [InlineData(0, 0, 10, 10, 20, 20, 10, 10, 20, 20, 0, 0)]     // 不相交（空矩形保留交点原点）
    [InlineData(0, 0, 100, 100, 50, 50, 100, 100, 50, 50, 50, 50)] // 部分相交
    [InlineData(10, 10, 0, 5, 0, 0, 100, 100, 10, 10, 0, 0)]     // 零宽矩形交出空
    public void Intersect_cases(int ax, int ay, int aw, int ah, int bx, int by, int bw, int bh,
        int ex, int ey, int ew, int eh)
    {
        var r = CaptureMath.Intersect(new CaptureRect(ax, ay, aw, ah), new CaptureRect(bx, by, bw, bh));
        Assert.Equal(new CaptureRect(ex, ey, ew, eh), r);
    }

    [Fact]
    public void Intersect_containment()
    {
        var outer = new CaptureRect(0, 0, 1000, 1000);
        var inner = new CaptureRect(100, 100, 50, 50);
        Assert.Equal(inner, CaptureMath.Intersect(inner, outer));
    }
}
