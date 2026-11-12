namespace Pasteup.Core.Capture;

public readonly record struct CaptureRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>纯函数集：屏幕区域裁剪/求交/缩放。捕获与覆盖层共用的几何计算，全部可单测。</summary>
public static class CaptureMath
{
    public static CaptureRect Normalize(int x1, int y1, int x2, int y2) => new(
        Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1));

    /// <summary>拖拽小于阈值视为误触，返回空区域。</summary>
    public static CaptureRect NormalizeDrag(int x1, int y1, int x2, int y2, int minSize = 4)
    {
        var r = Normalize(x1, y1, x2, y2);
        return r.Width < minSize || r.Height < minSize ? new CaptureRect(r.X, r.Y, 0, 0) : r;
    }

    public static CaptureRect Intersect(CaptureRect a, CaptureRect b)
    {
        var x = Math.Max(a.X, b.X);
        var y = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right <= x || bottom <= y ? new CaptureRect(x, y, 0, 0) : new CaptureRect(x, y, right - x, bottom - y);
    }
}
