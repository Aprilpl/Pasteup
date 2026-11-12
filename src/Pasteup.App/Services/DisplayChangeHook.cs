using System.Windows;
using System.Windows.Interop;
using Pasteup.Core.Capture;

namespace Pasteup.App.Services;

/// <summary>
/// 显示器变化 → Monitors.Invalidate()：WM_DISPLAYCHANGE（拓扑增删/分辨率）与
/// WM_SETTINGCHANGE（缩放比、工作区）都是广播消息，message-only 窗收不到，须挂真实
/// 窗口的 HwndSource。各消费窗（面板/设置/贴图）OnSourceInitialized 调 Attach——窗口
/// 互斥存活，任一在世即可收到；全部死亡的间隙由消费窗重建时的 Invalidate 兜底。
/// </summary>
public static class DisplayChangeHook
{
    public static void Attach(Window w)
    {
        if (HwndSource.FromHwnd(new WindowInteropHelper(w).Handle) is not { } src) return;
        src.AddHook((msg, _, _, _, ref handled) =>
        {
            if (msg is 0x001A or 0x007E) Monitors.Invalidate(); // WM_SETTINGCHANGE / WM_DISPLAYCHANGE
            return IntPtr.Zero;
        });
    }
}
