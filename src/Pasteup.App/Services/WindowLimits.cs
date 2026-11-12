using System.Windows;
using Pasteup.Core.Capture;

namespace Pasteup.App.Services;

/// <summary>
/// 窗口尺寸上限：宽 ≤ 所在屏工作区 50%，高 ≤ 工作区 100%（DIP）——用户定稿方向。
/// 此前两窗正好相反（宽不限、高封顶 680/720）；WindowDragging 每次移动实时读窗口 Max，
/// 故上限动态更新即刻生效。上限永不小于窗口 Min：WPF 要求 Min ≤ Max，小屏下 50% 工作区
/// 可能窄于 Min，倒挂会抛布局异常，取 Max 而非压 Min（Min 是可用性下限，不妥协）。
/// </summary>
public static class WindowLimits
{
    /// <summary>纯函数：由显示器（含工作区）与窗口下限求 DIP 上限。工作区缺省(0)回退全屏矩形。</summary>
    public static (double MaxWidth, double MaxHeight) Caps(MonitorRect m, double minWidth, double minHeight)
    {
        var dip = 96.0 / m.Dpi;
        var workW = m.WorkWidth > 0 ? m.WorkWidth : m.Width;
        var workH = m.WorkHeight > 0 ? m.WorkHeight : m.Height;
        return (Math.Max(minWidth, Math.Round(workW * dip * 0.5)), Math.Max(minHeight, Math.Round(workH * dip)));
    }

    /// <summary>窗口中心点所在显示器（中心 DIP × 当前 scale → 物理坐标；枚举不到回退主屏）。</summary>
    public static MonitorRect? MonitorOfWindowCenter(Window w)
    {
        var scale = System.Windows.PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var cx = (int)((w.Left + w.ActualWidth / 2) * scale);
        var cy = (int)((w.Top + w.ActualHeight / 2) * scale);
        return Monitors.ContainingPhysicalPoint(cx, cy) ?? Monitors.Primary();
    }

    /// <summary>按窗口中心所在显示器刷新其 MaxWidth/MaxHeight；挂 LocationChanged 即随拖动跨屏自动更新。</summary>
    public static void ApplyTo(Window w)
    {
        var m = MonitorOfWindowCenter(w);
        if (m is null) return;
        var minW = double.IsNaN(w.MinWidth) ? 0 : w.MinWidth;
        var minH = double.IsNaN(w.MinHeight) ? 0 : w.MinHeight;
        (w.MaxWidth, w.MaxHeight) = Caps(m, minW, minH);
    }
}
