using Pasteup.Core.Capture;

namespace Pasteup.App.Services;

/// <summary>空格预览窗口落点数学（纯函数，供单测）：目标显示器上水平居中、垂直 46% 锚定（原型 left:53%→居中 / top:47%）。
/// 输入为物理像素矩形与显示器 DPI，输出为该窗口应使用的 DIP Left/Top。</summary>
public static class PreviewLayout
{
    /// <summary>窗口在目标屏内水平居中的 Left（窗口比屏宽则贴左缘）。</summary>
    public static double CenterLeft(MonitorRect m, double dpi, double windowWidth)
    {
        var scale = 96.0 / dpi;
        var areaW = m.Width * scale;
        var left = m.X * scale + (areaW - windowWidth) / 2;
        var maxLeft = m.X * scale + Math.Max(0, areaW - windowWidth);
        return Math.Clamp(left, m.X * scale, maxLeft);
    }

    /// <summary>垂直中心锚定在屏高 46% 处的 Top，并钳制整窗在屏内（内容增高向下生长的初值）。</summary>
    public static double AnchorTop(MonitorRect m, double dpi, double windowHeight)
    {
        var scale = 96.0 / dpi;
        var top = m.Y * scale + m.Height * scale * 0.46 - windowHeight / 2;
        return ClampTopWithin(m, dpi, top, windowHeight);
    }

    /// <summary>把既有 Top 钳制回屏内（窗口高度变化后防溢出下缘）。</summary>
    public static double ClampTopWithin(MonitorRect m, double dpi, double top, double windowHeight)
    {
        var scale = 96.0 / dpi;
        var areaH = m.Height * scale;
        var maxTop = m.Y * scale + Math.Max(0, areaH - windowHeight);
        return Math.Clamp(top, m.Y * scale, maxTop);
    }

    /// <summary>遮罩整屏覆盖矩形（DIP）：预览打开时盖住面板所在屏的全部。</summary>
    public static (double Left, double Top, double Width, double Height) CoverRect(MonitorRect m, double dpi)
    {
        var scale = 96.0 / dpi;
        return (m.X * scale, m.Y * scale, m.Width * scale, m.Height * scale);
    }
}
