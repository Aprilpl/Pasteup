using System.Runtime.InteropServices;
using Pasteup.Core.Interop;

namespace Pasteup.Core.Capture;

/// <summary>一台物理显示器（物理像素坐标）与 DPI；Work* 为排除任务栏后的工作区
/// （枚举失败时为 0，消费方需回退全屏矩形）。</summary>
public sealed record MonitorRect(string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary, uint Dpi,
    int WorkX = 0, int WorkY = 0, int WorkWidth = 0, int WorkHeight = 0);

/// <summary>枚举物理显示器（物理像素坐标）与各自 DPI。带缓存：全套 P/Invoke
/// （EnumDisplayMonitors+GetMonitorInfo+GetDpiForMonitor）在面板/设置窗拖动期每次
/// LocationChanged 都会查询（WindowLimits.ApplyTo），逐事件枚举是纯浪费。拓扑/缩放
/// 变化由 WM_DISPLAYCHANGE/WM_SETTINGCHANGE 钩子与各消费窗创建时 Invalidate；
/// 返回的列表为缓存共享实例，调用方只读、不得修改。</summary>
public static class Monitors
{
    private static List<MonitorRect>? _cached;

    /// <summary>显示器拓扑或缩放变化后调用；下次 All() 重新枚举。</summary>
    public static void Invalidate() => Volatile.Write(ref _cached, null);

    public static List<MonitorRect> All()
    {
        var snapshot = Volatile.Read(ref _cached);
        if (snapshot is not null) return snapshot;
        var list = Enumerate();
        Volatile.Write(ref _cached, list);
        return list;
    }

    private static List<MonitorRect> Enumerate()
    {
        var list = new List<MonitorRect>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, ref rc, _) =>
        {
            var info = new NativeMethods.MONITORINFOEX { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
            if (NativeMethods.GetMonitorInfo(hMon, ref info))
            {
                uint dpi = 96;
                try { if (NativeMethods.GetDpiForMonitor(hMon, 0, out var dx, out _) == 0) dpi = dx; } catch { /* shcore 缺失时退回 96 */ }
                list.Add(new MonitorRect(
                    info.szDevice,
                    info.rcMonitor.Left, info.rcMonitor.Top,
                    info.rcMonitor.Right - info.rcMonitor.Left,
                    info.rcMonitor.Bottom - info.rcMonitor.Top,
                    (info.dwFlags & 1) != 0, dpi,
                    info.rcWork.Left, info.rcWork.Top,
                    info.rcWork.Right - info.rcWork.Left,
                    info.rcWork.Bottom - info.rcWork.Top));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static MonitorRect? Primary() => All().FirstOrDefault(m => m.IsPrimary);

    public static MonitorRect? ContainingPhysicalPoint(int x, int y) =>
        All().FirstOrDefault(m => x >= m.X && x < m.X + m.Width && y >= m.Y && y < m.Y + m.Height);
}
