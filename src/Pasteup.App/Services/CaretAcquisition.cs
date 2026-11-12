namespace Pasteup.App.Services;

/// <summary>caret 的来源（诊断/日志用）：native 系统 caret → UIA TextPattern → MSAA IAccessible。</summary>
public enum CaretSource
{
    /// <summary>GetGUIThreadInfo.rcCaret（经典 Win32 编辑控件 / RichEdit / Sublime 等）。</summary>
    Native = 0,

    /// <summary>UI Automation TextPattern 选区矩形（Chrome / Edge / Electron / VS Code 等自绘光标）。</summary>
    Uia = 1,

    /// <summary>MSAA IAccessible OBJID_CARET（Ditto 3.25 的 Chrome 修复同款；部分是 Qt/WinForms 控件）。</summary>
    Msaa = 2,
}

/// <summary>一次成功的 caret 采样。Caret 为**物理像素屏幕坐标**（已做客户区换算）。</summary>
public readonly record struct CaretHit(Win32Input.PhysicalRect Caret, CaretSource Source, string Detail);

/// <summary>单个 caret 来源探针（接口化：单测注入假实现，实机注入 Win32/UIA 实现）。</summary>
public interface ICaretProbe
{
    CaretSource Source { get; }

    /// <summary>取到返回 true 并给出屏幕坐标矩形；取不到时 why 说明原因（写诊断日志）。</summary>
    bool TryGet(IntPtr foregroundWindow, out Win32Input.PhysicalRect caret, out string why);
}

/// <summary>采集上下文：前台窗口、焦点窗口矩形（可空）、以及「点是否落在任一显示器内」判定。</summary>
public sealed record CaretProbeContext(
    IntPtr ForegroundWindow,
    Win32Input.PhysicalRect? FocusWindowRect,
    Func<int, int, bool> IsOnAnyMonitor);

/// <summary>
/// caret 校验（纯函数，单测覆盖）。两条硬约束：
/// ①尺寸像一根光标（不是整块控件、也不是 0×0 假 rect）；
/// ②确实落在焦点控件矩形内（±2px 容差）且中心在显示器上。
/// 2026-11-10 实测：Chrome 的 `rcCaret=(0,0,1,1)` 假 caret 曾把面板拉到屏幕角落，尺寸校验是承重墙；
/// 2026-11-09 的「客户区坐标当屏幕坐标用」事故则由 <see cref="Translate"/> 修复。
/// </summary>
public static class CaretValidation
{
    /// <summary>caret 宽度上限：插入光标是细竖线（有的应用报 0 宽，也接受）。</summary>
    public const int MaxCaretWidth = 8;

    /// <summary>caret 高度下限：至少一个字形高度。</summary>
    public const int MinCaretHeight = 8;

    /// <summary>caret 高度上限：一行高；整块控件矩形/整窗矩形会被这里挡掉。</summary>
    public const int MaxCaretHeight = 300;

    /// <summary>caret 允许贴边溢出的容差（物理像素）。</summary>
    public const int FocusTolerance = 2;

    /// <summary>宽高是否像一根插入光标。</summary>
    public static bool SizeLooksLikeCaret(int width, int height) =>
        width is >= 0 and <= MaxCaretWidth && height is >= MinCaretHeight and <= MaxCaretHeight;

    /// <summary>caret 是否落在焦点控件矩形内（含容差）。</summary>
    public static bool InsideFocus(Win32Input.PhysicalRect caret, Win32Input.PhysicalRect focus,
        int tolerance = FocusTolerance) =>
        caret.Left >= focus.Left - tolerance
        && caret.Top >= focus.Top - tolerance
        && caret.Right <= focus.Right + tolerance
        && caret.Bottom <= focus.Bottom + tolerance;

    /// <summary>
    /// 客户区矩形 + 客户区原点（屏幕坐标）→ 屏幕矩形。
    /// 2026-11-10 修：`GetGUIThreadInfo.rcCaret` 是 **hwndCaret 的客户区坐标**，之前直接当屏幕坐标用，
    /// 导致「校验时拿客户区矩形比焦点控件屏幕矩形」几乎必然 reject（窗口不在屏幕零点时），
    /// 于是每次唤起都退回鼠标——这就是编辑器里「跟鼠标」体感的主因。
    /// </summary>
    public static Win32Input.PhysicalRect Translate(Win32Input.PhysicalRect client, int originX, int originY) =>
        new(client.Left + originX, client.Top + originY, client.Right + originX, client.Bottom + originY);
}

/// <summary>
/// caret 采集链：按顺序问各来源，第一个通过校验的胜出。
/// 纯编排（不碰 Win32），单测用假探针锁死顺序/校验/回退语义。
/// </summary>
public static class CaretAcquisition
{
    /// <summary>采集链默认顺序：native（无跨进程调用，最快最准）→ UIA（自绘光标）→ MSAA（兜底）。</summary>
    public static IReadOnlyList<ICaretProbe> DefaultProbes { get; } = new ICaretProbe[]
    {
        new NativeCaretProbe(),
        new UiaCaretProbe(),
        new MsaaCaretProbe(),
    };

    /// <summary>首落位专用：只含 native 探针（GetGUIThreadInfo，本进程调用，微秒级）。
    /// 唤起路径在 Show() 之前只做得起这一步；UIA/MSAA 是跨进程调用（UIA 有 150ms 等待、
    /// MSAA 可挂起），挪到面板可见之后由 <see cref="UiaCaretProbeThread.ProbeAsync"/> 的
    /// 专用 STA 探针线程精修（复审问题1，2026-11-10；异步链的探针集合在 UiaCaretProbeThread 内部）。</summary>
    public static IReadOnlyList<ICaretProbe> NativeOnlyProbes { get; } = new ICaretProbe[]
    {
        new NativeCaretProbe(),
    };

    public static CaretHit? TryAcquire(CaretProbeContext context, IReadOnlyList<ICaretProbe> probes,
        out string trace)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var probe in probes)
        {
            bool got;
            Win32Input.PhysicalRect caret = default;
            string why;
            try
            {
                got = probe.TryGet(context.ForegroundWindow, out caret, out why);
            }
            catch (Exception ex)
            {
                // 探针内部异常不得影响唤起：记为原因后继续下一个来源
                got = false;
                why = "exception:" + ex.GetType().Name;
            }

            if (!got)
            {
                Append(sb, probe.Source, why);
                continue;
            }

            var width = caret.Width;
            var height = caret.Height;
            if (!CaretValidation.SizeLooksLikeCaret(width, height))
            {
                Append(sb, probe.Source, $"reject:size {width}x{height}");
                continue;
            }

            if (context.FocusWindowRect is { } focus && !CaretValidation.InsideFocus(caret, focus))
            {
                Append(sb, probe.Source, $"reject:outside-focus caret={Fmt(caret)} focus={Fmt(focus)}");
                continue;
            }

            var cx = (caret.Left + caret.Right) / 2;
            var cy = (caret.Top + caret.Bottom) / 2;
            if (!context.IsOnAnyMonitor(cx, cy))
            {
                Append(sb, probe.Source, $"reject:off-monitor center=({cx},{cy})");
                continue;
            }

            Append(sb, probe.Source, "ok");
            trace = sb.ToString();
            return new CaretHit(caret, probe.Source, why);
        }

        trace = sb.ToString();
        return null;
    }

    private static void Append(System.Text.StringBuilder sb, CaretSource source, string note)
    {
        if (sb.Length > 0) sb.Append("; ");
        sb.Append(source switch
        {
            CaretSource.Native => "native",
            CaretSource.Uia => "uia",
            CaretSource.Msaa => "msaa",
            _ => source.ToString(),
        });
        sb.Append(':').Append(note);
    }

    internal static string Fmt(Win32Input.PhysicalRect r) =>
        $"({r.Left},{r.Top},{r.Right},{r.Bottom})";
}
