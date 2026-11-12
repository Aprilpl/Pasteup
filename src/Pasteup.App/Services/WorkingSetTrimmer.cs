using System.Runtime.InteropServices;
using System.Text;

namespace Pasteup.App.Services;

/// <summary>
/// 空闲修剪的状态机：每个 tick 报告「本进程是否有可见窗口」，决定是否该修剪。
/// 可见期间重新武装，全部隐藏后的第一个 tick 修剪且本轮空闲只修剪一次
/// （用户快速开关面板不会反复修剪 churn）。
/// </summary>
public sealed class IdleTrimState
{
    private bool _trimmedSinceIdle;

    /// <summary>返回本 tick 是否应执行修剪。</summary>
    public bool OnTick(bool anyVisibleWindow)
    {
        if (anyVisibleWindow)
        {
            _trimmedSinceIdle = false;
            return false;
        }
        if (_trimmedSinceIdle) return false;
        _trimmedSinceIdle = true;
        return true;
    }
}

/// <summary>
/// 常驻内存瘦身：所有窗口都不可见（纯托盘驻留）时 GC 收一遍 + 修剪工作集。
/// 2026-11-03 基线实测（.JCODE/mem-opt/baseline.ps1）：面板用完收起后 WS ~173MB 驻留
/// （托管堆仅 ~5MB，其余全是 WPF 首开框架成本 + 已触碰页）；EmptyWorkingSet 后 30MB，
/// 再唤起仅回弹 68MB、唤起延迟 146ms vs 未修剪 144ms——软缺页从待机列表拿回，零代价。
/// 私有提交 ~117MB 是框架一次性成本不动：任务管理器默认列显示的是工作集，
/// 修剪后的待机页物理上也可被系统回收。贴图窗可见时不修剪（悬停交互下反复软缺页不划算）。
/// </summary>
public static class WorkingSetTrimmer
{
    /// <summary>
    /// 本进程是否存在可见的 WPF 顶层窗口。集中用窗口枚举判定而非逐窗口挂钩：
    /// 面板/设置/预览/遮罩/贴图/截图覆盖层/MessageBox，以后新增窗口零遗漏。
    /// WPF 顶层窗口类名带 HwndWrapper 前缀；托盘图标用的是隐藏 Win32 窗口不算。
    /// </summary>
    public static bool AnyVisibleOwnWindow()
    {
        var pid = (uint)Environment.ProcessId;
        var found = false;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var wpid);
            if (wpid == pid && IsWindowVisible(h))
            {
                var sb = new StringBuilder(64);
                _ = GetClassName(h, sb, 64);
                if (sb.ToString().StartsWith("HwndWrapper", StringComparison.Ordinal))
                {
                    found = true;
                    return false; // 停止枚举
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// GC 收两遍（缩略图 LRU 逐出的 BitmapSource 挂着原生 WIC 帧，靠终结器回收；
    /// 先收编，原生页才能随修剪一并逐出工作集）+ 清空工作集。
    /// 仅在窗口全隐藏的空闲边沿调用；几 MB 的堆 gen2 收集是毫秒级，面板隐藏期无卡顿面。
    /// </summary>
    public static void Trim()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        _ = EmptyWorkingSet(GetCurrentProcess());
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int count);

    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}
