using System.Windows;
using Pasteup.App.Services;
using Xunit;
using static Pasteup.App.Services.Win32Input;

namespace Pasteup.Tests;

/// <summary>无边框窗口边缘命中数学（Win32Input.EdgeHitTest）的单测。
/// 窗口 300×200、band=14 时：X&lt;14 左带、X&gt;286 右带、Y&lt;14 上带、Y&gt;186 下带。</summary>
public class EdgeResizeTests
{
    private static EdgeZone Hit(double w, double h, double x, double y, double band = 14)
        => Win32Input.EdgeHitTest(new Size(w, h), new Point(x, y), band);

    [Fact]
    public void CenterPoint_IsNotEdge()
        => Assert.Equal(EdgeZone.None, Hit(300, 200, 150, 100));

    [Theory]
    [InlineData(5, 100, EdgeZone.Left)]
    [InlineData(295, 100, EdgeZone.Right)]
    [InlineData(100, 5, EdgeZone.Top)]
    [InlineData(100, 195, EdgeZone.Bottom)]
    public void EdgeBand_HitsCorrespondingEdge(double x, double y, EdgeZone expected)
        => Assert.Equal(expected, Hit(300, 200, x, y));

    [Theory]
    [InlineData(5, 5, EdgeZone.TopLeft)]
    [InlineData(295, 5, EdgeZone.TopRight)]
    [InlineData(5, 195, EdgeZone.BottomLeft)]
    [InlineData(295, 195, EdgeZone.BottomRight)]
    public void Corner_Band_TakesPriorityOverEdge(double x, double y, EdgeZone expected)
        => Assert.Equal(expected, Hit(300, 200, x, y));

    [Fact]
    // X=14.5 刚出带（带为半开区间 [0,14) / (286,300]）
    public void PointJustOutsideBand_IsNotEdge()
        => Assert.Equal(EdgeZone.None, Hit(300, 200, 14.5, 100));

    [Fact]
    public void NonPositiveBand_OrEmptySize_ReturnsNone()
    {
        Assert.Equal(EdgeZone.None, Hit(300, 200, 5, 5, 0));
        Assert.Equal(EdgeZone.None, Hit(0, 0, 0, 0));
    }

    [Fact]
    public void TinyWindow_DegeneratesToCorner_WithoutError()
        // 宽高均小于 2×band 时整体落入角带，不允许抛异常
        => Assert.Equal(EdgeZone.TopLeft, Hit(10, 10, 5, 5));
}

/// <summary>手动拖动/缩放引擎的矩形推导数学（WindowDragging.ApplyDrag）。</summary>
public class ApplyDragTests
{
    private static (double L, double T, double W, double H) Apply(
        EdgeZone zone, double l, double t, double w, double h, double dx, double dy,
        double minW = 40, double minH = 30, double maxW = double.PositiveInfinity, double maxH = double.PositiveInfinity)
    {
        WindowDragging.ApplyDrag(zone, l, t, w, h, dx, dy, minW, minH, maxW, maxH,
            out var nl, out var nt, out var nw, out var nh);
        return (nl, nt, nw, nh);
    }

    [Fact]
    public void Move_ShiftsLeftTop_KeepsSize()
    {
        var r = Apply(EdgeZone.None, 100, 200, 300, 400, 30, -20);
        Assert.Equal((130, 180, 300, 400), r);
    }

    [Fact]
    public void RightEdge_GrowsWidth_LeftFixed()
    {
        var r = Apply(EdgeZone.Right, 100, 200, 300, 400, 50, 99);
        Assert.Equal((100, 200, 350, 400), r);
    }

    [Fact]
    public void BottomRightCorner_GrowsBoth()
    {
        var r = Apply(EdgeZone.BottomRight, 100, 200, 300, 400, 40, 30);
        Assert.Equal((100, 200, 340, 430), r);
    }

    [Fact]
    public void LeftEdge_PullLeft_GrowsWidthAndMovesLeft()
    {
        var r = Apply(EdgeZone.Left, 100, 200, 300, 400, -60, 0);
        Assert.Equal((40, 200, 360, 400), r);
    }

    [Fact]
    public void TopEdge_PullUp_GrowsHeightAndMovesUp()
    {
        var r = Apply(EdgeZone.Top, 100, 200, 300, 400, 0, -50);
        Assert.Equal((100, 150, 300, 450), r);
    }

    [Fact]
    public void Resize_ClampsToMin()
    {
        var r = Apply(EdgeZone.Right, 100, 200, 300, 400, -1000, 0, minW: 100);
        Assert.Equal((100, 200, 100, 400), r);
    }

    [Fact]
    public void Resize_ClampsToMax()
    {
        var r = Apply(EdgeZone.BottomRight, 100, 200, 300, 400, 1000, 1000, maxW: 500, maxH: 600);
        Assert.Equal((100, 200, 500, 600), r);
    }

    [Fact]
    public void LeftEdge_ClampedByMin_KeepsRightEdgeFixed()
    {
        // 左缘向右推 1000：宽钳到 100，左缘 = l0 + (w0 - 100)，右缘不动
        var r = Apply(EdgeZone.Left, 100, 200, 300, 400, 1000, 0, minW: 100);
        Assert.Equal((300, 200, 100, 400), r);
    }

    private static (double L, double T, double W, double H) ApplyAspect(
        EdgeZone zone, double l, double t, double w, double h, double dx, double dy, double aspect,
        double minW = 40, double minH = 30, double maxW = double.PositiveInfinity, double maxH = double.PositiveInfinity)
    {
        WindowDragging.ApplyDrag(zone, l, t, w, h, dx, dy, minW, minH, maxW, maxH, aspect,
            out var nl, out var nt, out var nw, out var nh);
        return (nl, nt, nw, nh);
    }

    [Fact]
    public void AspectLock_RightEdge_HeightFollowsWidth()
    {
        // 300×150 → 右缘 +100：宽 400，高按 0.5 比例随到 200
        var r = ApplyAspect(EdgeZone.Right, 0, 0, 300, 150, 100, 999, 0.5);
        Assert.Equal((0, 0, 400, 200), r);
    }

    [Fact]
    public void AspectLock_TopLeftCorner_AnchorsBottomRight()
    {
        // 左上角向左上拉 (-100,-100)：宽 300→400、高 150→200，L/T 同步补偿，右下角不动
        var r = ApplyAspect(EdgeZone.TopLeft, 100, 100, 300, 150, -100, -100, 0.5);
        Assert.Equal((0, 50, 400, 200), r);
    }

    [Fact]
    public void AspectLock_BottomEdge_WidthFollowsHeight()
    {
        // 下缘 +150：高 150→300，宽随比例 300→600
        var r = ApplyAspect(EdgeZone.Bottom, 0, 0, 300, 150, 999, 150, 0.5);
        Assert.Equal((0, 0, 600, 300), r);
    }
}
