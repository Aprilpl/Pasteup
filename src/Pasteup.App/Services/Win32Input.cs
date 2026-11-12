using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace Pasteup.App.Services;

public readonly record struct PhysicalPoint(int X, int Y);

/// <summary>App 层的窗口/输入互操作（贴图点击穿透、DWM 圆角、光标位置）。</summary>
public static class Win32Input
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    /// <summary>物理像素矩形（落位用）。</summary>
    public readonly record struct PhysicalRect(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    public static PhysicalPoint GetCursorPos() => GetCursorPos(out var p) ? new(p.X, p.Y) : new(0, 0);

    // ===== 文本插入光标（落位锚点的优先来源）=====
    // 只有真正调用 SetCaretPos 的控件才有效（经典 Win32 编辑框、RichEdit、Sublime 等）；Chrome / Electron /
    // 自绘光标应用返回 hwndCaret=0 或 rcCaret 全 0——「取不到」可被可靠识别，调用方据此降级到 UIA/鼠标。
    // 2026-11-09 实测与方向见 docs/ideas/panel-anchor-placement.md；2026-11-10 修：rcCaret 是客户区坐标。

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    /// <summary>
    /// 取前台线程的系统 caret 矩形（**物理像素屏幕坐标**；false = 该线程没有 native caret）。
    /// 注意：`GetGUIThreadInfo.rcCaret` 是 hwndCaret 的**客户区坐标**，必须用 hwndCaret 换算到屏幕——
    /// 2026-11-10 实测根因：旧实现直接把客户区矩形当屏幕坐标交给校验，窗口不在屏幕零点时必然被
    /// 「caret 必须在焦点控件内」挡掉，于是每次都退回鼠标。
    /// </summary>
    public static bool TryGetCaretScreen(IntPtr foregroundWindow, out PhysicalRect caretScreen, out string why)
    {
        caretScreen = default;
        why = "";
        if (foregroundWindow == IntPtr.Zero) { why = "no-foreground"; return false; }
        var tid = GetWindowThreadProcessId(foregroundWindow, out _);
        if (tid == 0) { why = "no-thread"; return false; }
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo((uint)tid, ref info)) { why = "GetGUIThreadInfo-failed"; return false; }
        if (info.hwndCaret == IntPtr.Zero) { why = "no-caret-hwnd"; return false; }

        var r = info.rcCaret;
        var (w, h) = (r.Right - r.Left, r.Bottom - r.Top);
        if (!CaretValidation.SizeLooksLikeCaret(w, h)) { why = $"bad-size {w}x{h}"; return false; }

        var tl = new POINT { X = r.Left, Y = r.Top };
        var br = new POINT { X = r.Right, Y = r.Bottom };
        if (!ClientToScreen(info.hwndCaret, ref tl) || !ClientToScreen(info.hwndCaret, ref br))
        {
            why = "ClientToScreen-failed";
            return false;
        }

        caretScreen = new PhysicalRect(tl.X, tl.Y, br.X, br.Y);
        why = $"hwndCaret=0x{info.hwndCaret:X} client=({r.Left},{r.Top},{r.Right},{r.Bottom})";
        return true;
    }

    /// <summary>前台线程的焦点窗口矩形（屏幕坐标）；无焦点/最小化（宽高为 0）返回 false。</summary>
    public static bool TryGetFocusWindowRect(IntPtr foregroundWindow, out PhysicalRect focusRect)
    {
        focusRect = default;
        if (foregroundWindow == IntPtr.Zero) return false;
        var tid = GetWindowThreadProcessId(foregroundWindow, out _);
        if (tid == 0) return false;
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo((uint)tid, ref info)) return false;
        return TryGetWindowRect(info.hwndFocus, out focusRect);
    }

    // ===== MSAA（IAccessible OBJID_CARET）=====
    // Ditto 3.25 为 Chrome 加的同一路（PR #593）；对自绘编辑器大多无效，但能覆盖部分是 Qt/WinForms 控件。
    // 实测要点：优先问 hwndFocus（记事本的 RichEdit 只有焦点窗口这一问能答），再问前台窗口。

    private const uint OBJID_CARET = 0xFFFFFFF8;

    [StructLayout(LayoutKind.Sequential)]
    private struct VARIANT
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr data1;
        public IntPtr data2;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int AccLocationFn(IntPtr self, out int left, out int top, out int width, out int height,
        VARIANT varChild);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseFn(IntPtr self);

    [DllImport("oleacc.dll")]
    private static extern int AccessibleObjectFromWindow(IntPtr hwnd, uint dwId, ref Guid riid, out IntPtr ppvObject);

    private static readonly Guid IidIAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

    [DllImport("user32.dll")]
    private static extern bool IsHungAppWindow(IntPtr hWnd);

    /// <summary>窗口是否已被系统判定「无响应」（约 5s 没有处理消息）。
    /// 跨进程无障碍调用前的快检：MSAA 的 AccessibleObjectFromWindow/accLocation 没有超时，
    /// 向僵死窗口索取 caret 会让调用线程无限期挂住（复审问题1）。</summary>
    public static bool IsWindowHung(IntPtr hwnd) => hwnd != IntPtr.Zero && IsHungAppWindow(hwnd);

    /// <summary>问 MSAA 要 caret 矩形（返回屏幕坐标）；hwndFocus 优先，再问前台窗口。</summary>
    public static bool TryGetAccessibleCaretScreen(IntPtr foregroundWindow, out PhysicalRect caret, out string why)
    {
        caret = default;
        why = "";
        if (foregroundWindow == IntPtr.Zero) { why = "no-foreground"; return false; }
        // 挂起快检：僵死前台的 MSAA 查询可能永不返回（无超时）——直接跳过，让采集链继续回退，
        // why 记 hung-skip 供 placement.log 诊断（2026-11-10 复审问题1）
        if (IsWindowHung(foregroundWindow)) { why = "hung-skip"; return false; }

        var tried = new List<IntPtr>(2);
        // 焦点窗口优先（子控件自绘 caret 常挂在这里）；去重后追加前台窗口
        if (TryGetFocusHwnd(foregroundWindow, out var focus) && focus != IntPtr.Zero) tried.Add(focus);
        if (!tried.Contains(foregroundWindow)) tried.Add(foregroundWindow);

        var reasons = new System.Text.StringBuilder();
        foreach (var hwnd in tried)
        {
            if (TryQueryAccessibleCaret(hwnd, out var raw, out var note))
            {
                // Ditto 的判据：left/top 非 0 才采信（(0,0,·,·) 是「没有 caret」的常见假值）
                if (raw.Left != 0 && raw.Top != 0)
                {
                    if (!CaretValidation.SizeLooksLikeCaret(raw.Width, raw.Height))
                    {
                        AppendReason(reasons, $"0x{hwnd:X}:bad-size {raw.Width}x{raw.Height}");
                        continue;
                    }
                    caret = raw;
                    why = $"hwnd=0x{hwnd:X} {note}";
                    return true;
                }
                AppendReason(reasons, $"0x{hwnd:X}:zero-origin rect=({raw.Left},{raw.Top},{raw.Right},{raw.Bottom})");
            }
            else
            {
                AppendReason(reasons, $"0x{hwnd:X}:{note}");
            }
        }

        why = reasons.ToString();
        return false;
    }

    private static bool TryGetFocusHwnd(IntPtr foregroundWindow, out IntPtr focus)
    {
        focus = IntPtr.Zero;
        var tid = GetWindowThreadProcessId(foregroundWindow, out _);
        if (tid == 0) return false;
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo((uint)tid, ref info)) return false;
        focus = info.hwndFocus;
        return focus != IntPtr.Zero;
    }

    private static bool TryQueryAccessibleCaret(IntPtr hwnd, out PhysicalRect caret, out string note)
    {
        caret = default;
        note = "";
        if (hwnd == IntPtr.Zero) { note = "null-hwnd"; return false; }

        var iid = IidIAccessible;
        var hr = AccessibleObjectFromWindow(hwnd, OBJID_CARET, ref iid, out var ptr);
        if (hr != 0 || ptr == IntPtr.Zero) { note = $"AOFW hr=0x{hr:X8}"; return false; }
        try
        {
            var vtable = Marshal.ReadIntPtr(ptr);
            // IAccessible vtable：IUnknown(3) + IDispatch(4) + 15 个方法 = accLocation 在 22
            var fn = Marshal.ReadIntPtr(vtable, 22 * IntPtr.Size);
            var accLocation = Marshal.GetDelegateForFunctionPointer<AccLocationFn>(fn);
            var child = new VARIANT { vt = 3 /*VT_I4*/, data1 = IntPtr.Zero /*CHILDID_SELF*/ };
            // VARIANT 是 16 字节，IDL 里按值传（[in] VARIANT varChild）；按引用传会让被调方读到错位参数。
            var hr2 = accLocation(ptr, out var left, out var top, out var width, out var height, child);
            if (hr2 != 0) { note = $"accLocation hr=0x{hr2:X8}"; return false; }
            caret = new PhysicalRect(left, top, left + width, top + height);
            note = $"rect=({left},{top},{width},{height})";
            return true;
        }
        finally
        {
            var vtable = Marshal.ReadIntPtr(ptr);
            var release = Marshal.GetDelegateForFunctionPointer<ReleaseFn>(
                Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size));
            _ = release(ptr);
        }
    }

    private static void AppendReason(System.Text.StringBuilder sb, string note)
    {
        if (sb.Length > 0) sb.Append("; ");
        sb.Append(note);
    }

    /// <summary>窗口矩形（物理像素）；无句柄/最小化（宽高为 0）返回 false。</summary>
    public static bool TryGetWindowRect(IntPtr hwnd, out PhysicalRect rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return false;
        rect = new PhysicalRect(r.Left, r.Top, r.Right, r.Bottom);
        return rect.Width > 0 && rect.Height > 0;
    }

    /// <summary>按物理像素挪窗口（尺寸不动）：避开 WPF Left/Top 在跨 DPI 显示器上的语义坑。</summary>
    public static void SetWindowPosition(IntPtr hwnd, int x, int y)
    {
        if (hwnd == IntPtr.Zero) return;
        _ = SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>点击穿透：命中测试直接穿透到下层窗口（贴图「钉在屏幕上」模式）。</summary>
    public static void SetClickThrough(Window window, bool enabled)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        _ = SetWindowLong(hwnd, GWL_EXSTYLE,
            enabled ? style | WS_EX_LAYERED | WS_EX_TRANSPARENT : style & ~(WS_EX_LAYERED | WS_EX_TRANSPARENT));
    }

    private const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>点击不激活：窗口仍收鼠标输入，但被点击时不抢占前台/焦点
    /// （空格预览窗口用——面板 Deactivated 即隐藏，预览绝不能夺走激活）。</summary>
    public static void SetNoActivate(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        _ = SetWindowLong(hwnd, GWL_EXSTYLE, style | WS_EX_NOACTIVATE);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>把 window 钉到 z 序中 refWindow 的正下方（压其余窗口、被 refWindow 压）——
    /// 预览遮罩用：面板之上、预览之下。Topmost 带内新 Show 的窗口会窜到最顶，每次重钉保序。</summary>
    public static void PlaceBelow(Window window, Window refWindow)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        var refHwnd = new System.Windows.Interop.WindowInteropHelper(refWindow).Handle;
        if (hwnd == IntPtr.Zero || refHwnd == IntPtr.Zero) return;
        _ = SetWindowPos(hwnd, refHwnd, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void PreferRoundedCorners(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var pref = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    // ===== 无边框窗的系统阴影：纯 WS_POPUP 默认无投影；把系统帧扩进客户区 1px 即可获得
    // DWM 原生软阴影（帧画在客户区背后，被不透明内容盖住，无视觉残留）。
    // 同时关掉 DWM 自带 1px 系统描边——描边仍由 PopupBorder 令牌负责，令牌是唯一视觉源头。 =====

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS { public int CxLeftWidth, CxRightWidth, CyTopHeight, CyBottomHeight; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS pMarSet);

    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    /// <summary>给无边框不透明窗引出 DWM 系统阴影（需先/再 PreferRoundedCorners，圆角由系统裁出）。</summary>
    public static void EnableDwmShadow(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var margins = new MARGINS { CxLeftWidth = 0, CxRightWidth = 0, CyTopHeight = 0, CyBottomHeight = 1 };
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
        var noBorder = DWMWA_COLOR_NONE;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
    }

    // ===== 无边框窗口边缘命中数学（纯函数）=====
    // 曾经配过 WS_THICKFRAME + WM_NCHITTEST 的系统缩放路径，实测部分输入路径无法驱动
    // DefWindowProc 移动/缩放模态循环，已改由 WindowDragging 手动引擎实现；保留此纯函数供其复用。

    public enum EdgeZone { None, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    /// <summary>边缘命中数学（纯函数，供单测）：窗口相对坐标落在边缘带内的方位，四角优先于四边。
    /// 极小窗口（宽或高 &lt; 2×band）会整体落入角/边，属预期退化。</summary>
    public static EdgeZone EdgeHitTest(System.Windows.Size size, System.Windows.Point p, double band)
    {
        if (band <= 0 || size.Width <= 0 || size.Height <= 0) return EdgeZone.None;
        var onLeft = p.X < band;
        var onRight = p.X > size.Width - band;
        var onTop = p.Y < band;
        var onBottom = p.Y > size.Height - band;
        if (onTop && onLeft) return EdgeZone.TopLeft;
        if (onTop && onRight) return EdgeZone.TopRight;
        if (onBottom && onLeft) return EdgeZone.BottomLeft;
        if (onBottom && onRight) return EdgeZone.BottomRight;
        if (onLeft) return EdgeZone.Left;
        if (onRight) return EdgeZone.Right;
        if (onTop) return EdgeZone.Top;
        if (onBottom) return EdgeZone.Bottom;
        return EdgeZone.None;
    }

    // ===== 前台焦点还原（面板 Enter 粘贴链路）=====
    // 面板 Hide() 后依赖系统自动还原前台不可靠：目标窗口恢复前台有快有慢，
    // 固定等待后盲发 Ctrl+V 会落空或落错窗口。
    // 键盘合成（Alt 点放 / Esc）委托 Core.PasteSimulator.TapKey——INPUT 结构体与 SendInput
    // 封送全仓只此一份，本文件不持有第二份定义。

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(int idAttach, int idAttachTo, bool fAttach);

    private const ushort VK_MENU = 0x12;
    private const ushort VK_ESCAPE = 0x1B;

    /// <summary>合成一次 Alt 按放：解除 SetForegroundWindow 的前台锁（系统约定 Alt 键按下后放行）。
    /// 按放之间留间隔，过近时部分应用来不及处理。</summary>
    private static void TapAlt() => Core.Clipboard.PasteSimulator.TapKey(VK_MENU, 50);

    /// <summary>合成一次 Esc：TapAlt 可能把目标应用拉进菜单栏模式（菜单循环吞掉一切后续按键且
    /// 阻止激活，2026-11-02 冒烟实锤），Esc 是退出该模式的通用键。</summary>
    public static void SendEsc() => Core.Clipboard.PasteSimulator.TapKey(VK_ESCAPE, 30);

    /// <summary>窗口所属进程 PID（0 = 无效句柄）。</summary>
    public static uint ProcessIdOfWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return (uint)pid;
    }

    /// <summary>捕获当前前台窗口句柄（唤起面板前调用，回车粘贴时把焦点还给它）。</summary>
    public static IntPtr ForegroundWindow() => GetForegroundWindow();

    /// <summary>当前前台窗口是否仍归目标窗口所属的进程（pid 比对，防同 hwnd 复用误判）。
    /// 粘贴注入前的最后复核用——还原成功到 SendInput 之间有 60ms+ 窗口，前台可能已易主。</summary>
    public static bool ForegroundBelongsTo(IntPtr target)
    {
        if (target == IntPtr.Zero) return false;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(fg, out var fgPid);
        _ = GetWindowThreadProcessId(target, out var targetPid);
        return fgPid != 0 && fgPid == targetPid;
    }

    /// <summary>
    /// 粘贴注入前的最后复核（hwnd + pid 双证）：当前前台窗口所属 pid 必须等于**唤起时捕获的
    /// 目标 pid**。旧单参重载现场再读 target 的 pid，若 target 句柄已被系统复用给别的进程窗口，
    /// 复核会随新窗口自指通过（复审 P1-2）；固定 expectedPid 后 hwnd 复用也拦得住。
    /// </summary>
    public static bool ForegroundBelongsTo(IntPtr target, int expectedPid)
    {
        if (target == IntPtr.Zero || expectedPid == 0) return false;
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        _ = GetWindowThreadProcessId(fg, out var fgPid);
        return fgPid != 0 && fgPid == expectedPid;
    }

    /// <summary>
    /// 尽力把前台还给目标窗口的**异步**版本（在后台线程跑，UI 线程零阻塞）。
    /// 阶梯与旧同步实现完全等价：先直接 SetForegroundWindow（本进程持有前台时有权），再试
    /// AttachThreadInput 舞步，失败则合成 Alt 点放解锁后带重试再抢（舞步对打包应用无效——
    /// AttachThreadInput 直接返回 False，如 Win11 商店版记事本，2026-11-02 探针实锤）。
    /// 唯一的改动是 `Thread.Sleep` → `await Task.Delay`（轮次/顺序/判据一字不变）：
    /// 该阶梯被 ShowAtCursor 复用为「唤起后抢前台」，无输入路径（单实例转发/--show）下最坏
    /// ≈1.04s；同步 sleep 期间 UI 线程不在消息循环，面板已 Show 却无法渲染/响应（复审问题1）。
    /// AltTapUsed 告知调用方是否动用了 Alt 点放（此时目标应用可能残留菜单栏模式，
    /// 粘贴前应补一个 Esc）。Restored 表示是否确认还原成功（提权窗口 UIPI 下仍会失败——
    /// 后续按键同样会被其丢弃，调用方自决是否照发）。
    /// </summary>
    public static Task<(bool Restored, bool AltTapUsed)> RestoreForegroundAsync(IntPtr target)
        // 显式泛型实参选中 Func<Task<TResult>> 重载：lambda 直接返回 Task<T>（不 unwrap 的话
        // 会被推成嵌套 Task，公开签名就错了）——比依赖 lambda 返回类型的重载推断更明确
        => Task.Run<(bool Restored, bool AltTapUsed)>(() => RestoreForegroundCoreAsync(target));

    private static async Task<(bool Restored, bool AltTapUsed)> RestoreForegroundCoreAsync(IntPtr target)
    {
        if (target == IntPtr.Zero) return (false, false);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (GetForegroundWindow() == target) return (true, false);
            var fore = GetForegroundWindow();
            var foreThread = fore != IntPtr.Zero ? GetWindowThreadProcessId(fore, out _) : 0;
            _ = GetWindowThreadProcessId(target, out var targetThread);
            var attached = foreThread != 0 && foreThread != targetThread
                && AttachThreadInput(foreThread, targetThread, true);
            try
            {
                if (targetThread != 0) _ = SetForegroundWindow(target);
            }
            finally
            {
                if (attached) _ = AttachThreadInput(foreThread, targetThread, false);
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
        if (GetForegroundWindow() == target) return (true, false);
        for (var attempt = 0; attempt < 4; attempt++)
        {
            TapAlt();
            await Task.Delay(30).ConfigureAwait(false);
            _ = SetForegroundWindow(target);
            await Task.Delay(40).ConfigureAwait(false);
            if (GetForegroundWindow() == target) return (true, true);
            // Alt 可能把目标应用拉进菜单栏模式：Esc 关掉后趁解锁效果还在再抢一次
            SendEsc();
            await Task.Delay(30).ConfigureAwait(false);
            _ = SetForegroundWindow(target);
            await Task.Delay(40).ConfigureAwait(false);
            if (GetForegroundWindow() == target) return (true, true);
        }
        return (GetForegroundWindow() == target, true);
    }
}

/// <summary>托盘图标回退路径：嵌入 app.ico（Assets/app.ico）加载失败时用 DrawingVisual 画青底白 P。</summary>
public static class TrayIconPainter
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>产出可直接交给 TaskbarIcon.Icon 的 GDI 图标。
    /// 不能走 IconSource：H.NotifyIcon 的 ImageSource 转换只认 URI 来源的 BitmapImage/BitmapFrame，
    /// 代码绘制的位图会抛 "ImageSource type ... is not supported" 或 UriFormatException（2026-10-27 踩坑）。</summary>
    public static System.Drawing.Icon PaintIcon()
    {
        const int size = 32;
        var png = ImageCodec.RenderToPng(size, size, dc =>
        {
            // 品牌色从令牌层读取（令牌是唯一视觉源头）；资源不可用时回退原型青
            var accent = Application.Current?.TryFindResource("AccentBrush") as System.Windows.Media.SolidColorBrush
                         ?? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1B, 0x6C, 0x80));
            dc.DrawRoundedRectangle(accent, null, new Rect(1, 1, size - 2, size - 2), 8, 8);
            var text = new System.Windows.Media.FormattedText("P",
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily("Segoe UI"),
                    System.Windows.FontStyles.Normal, System.Windows.FontWeights.Bold, System.Windows.FontStretches.Normal),
                20, System.Windows.Media.Brushes.White, 96);
            dc.DrawText(text, new System.Windows.Point((size - text.Width) / 2, (size - text.Height) / 2 - 1));
        });

        // PNG → GDI+ → HICON；FromHandle 不持有句柄，Clone 出自持有的副本后再释放临时句柄
        using var ms = new MemoryStream(png);
        using var bmp = new System.Drawing.Bitmap(ms);
        var hIcon = bmp.GetHicon();
        try
        {
            return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            _ = DestroyIcon(hIcon);
        }
    }
}
