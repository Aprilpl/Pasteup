namespace Pasteup.App.Services;

/// <summary>native 系统 caret（GetGUIThreadInfo）探针：本进程 API，无跨进程调用，最快。</summary>
internal sealed class NativeCaretProbe : ICaretProbe
{
    public CaretSource Source => CaretSource.Native;

    public bool TryGet(IntPtr foregroundWindow, out Win32Input.PhysicalRect caret, out string why) =>
        Win32Input.TryGetCaretScreen(foregroundWindow, out caret, out why);
}

/// <summary>
/// UIA TextPattern 探针（Chrome / Edge / Electron / VS Code 等自绘光标的唯一可靠来源）。
/// 查询在专用后台 STA 线程执行并带超时：UIA 是跨进程调用，目标应用卡死时不能拖住 UI 线程。
/// 本探针是同步入口（DefaultProbes / --poc=caret 用）；面板唤起的异步精修链在
/// <see cref="UiaCaretProbeThread.ProbeAsync"/> 内直接调 <see cref="UiaTextCaret"/>（不走这里，避免自请求）。
/// </summary>
internal sealed class UiaCaretProbe : ICaretProbe
{
    public CaretSource Source => CaretSource.Uia;

    public bool TryGet(IntPtr foregroundWindow, out Win32Input.PhysicalRect caret, out string why)
    {
        caret = default;
        if (!UiaCaretProbeThread.Shared.TryGet(foregroundWindow, out var raw, out why)) return false;
        caret = raw;
        return true;
    }
}

/// <summary>MSAA IAccessible OBJID_CARET 探针（Ditto 3.25 同款；hwndFocus 优先）。
/// 跨进程 COM，入口自带 IsHungAppWindow 快检；异步链里在 <see cref="UiaCaretProbeThread"/> 的
/// STA 线程上执行，与 UIA 同一套 STA 上下文（复审问题1，2026-11-10）。</summary>
internal sealed class MsaaCaretProbe : ICaretProbe
{
    public CaretSource Source => CaretSource.Msaa;

    public bool TryGet(IntPtr foregroundWindow, out Win32Input.PhysicalRect caret, out string why) =>
        Win32Input.TryGetAccessibleCaretScreen(foregroundWindow, out caret, out why);
}
