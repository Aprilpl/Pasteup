using System.Runtime.InteropServices;

namespace Pasteup.App.Services;

/// <summary>
/// UI Automation TextPattern 取 caret（2026-11-10）：Chrome / Edge / Electron / VS Code 等自绘光标应用
/// **没有** native caret（GetGUIThreadInfo 全 0）、MSAA OBJID_CARET 也返回 S_FALSE，但 UIA 的
/// `FocusedElement → TextPattern.GetSelection() → GetBoundingRectangles()` 能给出 caret 矩形
/// （实测 Chrome：行首 (225,528,1,38)、行尾 (571,528,1,38)，与 Ditto 面板位置 (left+width, top+20) 吻合）。
///
/// 2026-11-13 修（Chrome 面板固定落位）：面板 Show 后再跑异步精修时全局焦点已被面板抢走，
/// `GetFocusedElement` 只能拿到面板自己的元素（pid 不匹配）→ 旧兜底「窗口里第一个 TextPattern 后代」
/// 在 Chrome 下永远命中地址栏 → 面板每次都落在同一位置。现改为在目标窗口树里按
/// `HasKeyboardFocus=TRUE` 找应用内部焦点元素（详见 <see cref="TryWindowKeyboardFocusedCaret"/>）。
///
/// 2026-11-14 补（搜索框内有光标、面板却跟鼠标）：Chrome/Electron 对**空输入框**/整行选区
/// 会把 TextPattern 选区矩形报成元素自身的矩形（不是细光标），被尺寸校验拒绝后同样退鼠标。
/// 现由 <see cref="TryElementCaretOrAnchor"/> 把「落在元素矩形内的整行矩形」退化为零宽锚点。
///
/// .NET 10 没有内置 System.Windows.Automation，这里手写最小 COM interop（IUIAutomation 是
/// IUnknown 派生的直连 vtable 接口，用槽位下标调用，避免为 50+ 个无关方法写声明）。
/// 只在 <see cref="UiaCaretProbeThread"/> 的 STA 工作线程上调用。
/// </summary>
internal static class UiaTextCaret
{
    // ---- COM 标识 ----
    private static readonly Guid ClsidCUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    private static readonly Guid IidIUIAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");

    // ---- 常量 ----
    private const int ClsctxInprocServer = 1;
    private const int CoinitApartmentThreaded = 0x2;
    private const int ScopeDescendants = 4;                 // TreeScope_Descendants
    private const int TextPatternId = 10014;                // UIA_TextPatternId
    private const int EditControlType = 50004;              // UIA_EditControlTypeId
    private const int HasKeyboardFocusPropertyId = 30008;   // UIA_HasKeyboardFocusPropertyId
    private const int IsTextPatternAvailablePropertyId = 30040;
    private const int VtBool = 11;
    private const int VtI4 = 3;

    // ---- vtable 槽位（IUnknown 占 0..2，随后按接口方法顺序）----
    // IUIAutomation: CompareElements 3, CompareRuntimeIds 4, GetRootElement 5, ElementFromHandle 6,
    //                ElementFromPoint 7, GetFocusedElement 8, ...（CreateTrueCondition 在 21，
    //                CreateFalseCondition 22，CreatePropertyCondition 23，CreatePropertyConditionEx 24）
    private const int UiAutomationElementFromHandle = 6;
    private const int UiAutomationGetFocusedElement = 8;
    private const int UiAutomationCreatePropertyCondition = 23;

    // IUIAutomationElement: SetFocus 3, GetRuntimeId 4, FindFirst 5, ..., GetCurrentPattern 16, ..., get_CurrentProcessId 20
    private const int ElementFindFirst = 5;
    private const int ElementGetCurrentPattern = 16;
    private const int ElementGetCurrentProcessId = 20;
    private const int ElementGetCurrentBoundingRectangle = 43; // get_CurrentBoundingRectangle

    // IUIAutomationTextPattern: RangeFromPoint 3, RangeFromChild 4, GetSelection 5
    private const int TextPatternGetSelection = 5;

    // IUIAutomationTextRangeArray: get_Length 3, GetElement 4
    private const int RangeArrayGetLength = 3;
    private const int RangeArrayGetElement = 4;

    // IUIAutomationTextRange: Clone 3, Compare 4, CompareEndpoints 5, ExpandToEnclosingUnit 6,
    //                          FindAttribute 7, FindText 8, GetAttributeValue 9, GetBoundingRectangles 10
    private const int TextRangeGetBoundingRectangles = 10;

    // ---- interop ----
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetFocusedElementFn(IntPtr self, out IntPtr element);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ElementFromHandleFn(IntPtr self, IntPtr hwnd, out IntPtr element);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreatePropertyConditionFn(IntPtr self, int propertyId, VARIANT value,
        out IntPtr condition);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FindFirstFn(IntPtr self, int scope, IntPtr condition, out IntPtr found);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCurrentPatternFn(IntPtr self, int patternId, out IntPtr pattern);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCurrentProcessIdFn(IntPtr self, out int processId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCurrentControlTypeFn(IntPtr self, out int controlType);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetCurrentClassNameFn(IntPtr self,
        [MarshalAs(UnmanagedType.BStr)] out string className);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBoundingRectangleFn(IntPtr self, out RECT rect);

    // IUIAutomationElement: get_CurrentProcessId 20, get_CurrentControlType 21, get_CurrentClassName 30
    private const int ElementGetCurrentControlType = 21;
    private const int ElementGetCurrentClassName = 30;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetSelectionFn(IntPtr self, out IntPtr rangeArray);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRangeCountFn(IntPtr self, out int count);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRangeElementFn(IntPtr self, int index, out IntPtr range);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBoundingRectanglesFn(IntPtr self, out IntPtr safeArray);

    [DllImport("ole32.dll")]
    private static extern int CoInitializeEx(IntPtr pvReserved, uint coInit);

    [DllImport("ole32.dll")]
    private static extern void CoUninitialize();

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint clsctx, ref Guid iid,
        out IntPtr ppv);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetLBound(IntPtr psa, uint dimension, out int lowerBound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayGetUBound(IntPtr psa, uint dimension, out int upperBound);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayAccessData(IntPtr psa, out IntPtr data);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayUnaccessData(IntPtr psa);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayDestroy(IntPtr psa);

    internal static void InitializeCom() => _ = CoInitializeEx(IntPtr.Zero, CoinitApartmentThreaded);

    internal static void UninitializeCom() => CoUninitialize();

    /// <summary>在当前（STA）线程创建 UIA 客户端。</summary>
    internal static bool TryCreateClient(out IntPtr uia, out string why)
    {
        uia = IntPtr.Zero;
        why = "";
        var clsid = ClsidCUIAutomation;
        var iid = IidIUIAutomation;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out var ptr);
        if (hr != 0 || ptr == IntPtr.Zero)
        {
            why = $"CoCreateInstance(CUIAutomation) hr=0x{hr:X8}";
            return false;
        }
        uia = ptr;
        return true;
    }

    /// <summary>
    /// 取前台窗口的 caret 矩形（屏幕坐标，物理像素）。
    /// 主路径：UIA 全局焦点元素（用户正在用的应用 = 前台时它最准）。
    /// 兜底：前台窗口后代里 **仍持有键盘焦点的元素**（面板 Show 后全局焦点已被面板抢走，
    /// 只能从目标窗口内部找回应用自己的焦点，2026-11-13 修）；再兜底「第一个
    /// IsTextPatternAvailable 后代」（老应用/无障碍树未激活时无 HasKeyboardFocus 信息）。
    /// </summary>
    internal static bool TryReadCaret(IntPtr uia, IntPtr foregroundWindow, int foregroundProcessId,
        out Win32Input.PhysicalRect caret, out string why)
    {
        caret = default;
        var focusedWhy = "";
        var windowWhy = "";

        if (TryReadFocusedCaret(uia, foregroundProcessId, out caret, out focusedWhy))
        {
            why = focusedWhy;
            return true;
        }

        if (foregroundWindow != IntPtr.Zero)
        {
            if (TryElementFromHandle(uia, foregroundWindow, out var root, out var rootNote))
            {
                try
                {
                    // ① 先问「窗口内仍持有键盘焦点的元素」：2026-11-13 根因修复。
                    //    面板 Show 之后全局焦点已被面板自己抢走，GetFocusedElement 拿到的元素 pid 不匹配
                    //    （或 pid 匹配但只是顶层 Window），旧逻辑于是退化为「窗口里第一个 TextPattern
                    //    后代」——Chrome 的树序里第一个恰是地址栏（OmniboxViewViews），其 caret 恒定，
                    //    面板因此每次都被精修到同一个固定位置。
                    //    HasKeyboardFocus 是**目标应用内部**的焦点属性（属主窗口失去前台后 Chrome /
                    //    Electron 仍报 TRUE，2026-11-13 实机探针验证），据此能把用户正在编辑的元素找回来。
                    switch (TryWindowKeyboardFocusedCaret(uia, root, out caret, out var kbdNote))
                    {
                        case WindowFocusProbe.FoundCaret:
                            why = "window:" + kbdNote;
                            return true;
                        case WindowFocusProbe.FoundNoCaret:
                            // 窗口里确实有持焦元素，但它（及子树）没有 TextPattern：焦点在按钮/非文本
                            // 控件上，本来就没有插入光标。此时**不再**用「第一个 TextPattern」兜底
                            //（那会命中地址栏一类无关编辑框、把面板钉在固定位置），交给 MSAA/鼠标。
                            windowWhy = kbdNote;
                            break;
                        default:
                            // 窗口树里没有任何元素报告键盘焦点（老应用/无障碍树未激活）：保留旧的
                            // 「第一个 TextPattern 后代」兜底，既有行为不回退。
                            windowWhy = kbdNote;
                            if (TryFirstTextElement(uia, root, out var textElement, out var findNote))
                            {
                                try
                                {
                                    if (TryElementCaretOrAnchor(textElement, out caret, out var note))
                                    {
                                        why = "window:" + note;
                                        return true;
                                    }
                                    findNote = "no-caret:" + note;
                                }
                                finally
                                {
                                    _ = Marshal.Release(textElement);
                                }
                            }
                            windowWhy += "; first-text:" + findNote;
                            break;
                    }
                }
                finally
                {
                    _ = Marshal.Release(root);
                }
            }
            else
            {
                windowWhy = rootNote;
            }
        }
        else
        {
            windowWhy = "no-foreground";
        }

        why = $"focused=({focusedWhy}); window=({windowWhy})";
        return false;
    }

    /// <summary>
    /// 只读「全局焦点元素」的 TextPattern caret；pid 必须等于前台进程（防抢焦点后把别人的元素当目标）。
    /// 供两条路复用：①采集链的焦点元素主路径；②面板 Show **之前**的焦点快照
    ///（<see cref="UiaCaretProbeThread.TryCaptureFocusedCaret"/>——Show 之后全局焦点已是面板自己）。
    /// </summary>
    internal static bool TryReadFocusedCaret(IntPtr uia, int foregroundProcessId,
        out Win32Input.PhysicalRect caret, out string why)
    {
        caret = default;
        if (!TryFocusedElement(uia, foregroundProcessId, out var focused, out var info))
        {
            why = info;
            return false;
        }

        try
        {
            if (TryElementCaretOrAnchor(focused, out caret, out var note))
            {
                why = $"focused({info}) -> {note}";
                return true;
            }
            why = $"focused({info}) no-caret:{note}";
            return false;
        }
        finally
        {
            _ = Marshal.Release(focused);
        }
    }

    private static bool TryFocusedElement(IntPtr uia, int foregroundProcessId, out IntPtr element,
        out string info)
    {
        element = IntPtr.Zero;
        info = "";
        var getFocused = GetFn<GetFocusedElementFn>(uia, UiAutomationGetFocusedElement);
        var hr = getFocused(uia, out var focused);
        if (hr != 0 || focused == IntPtr.Zero)
        {
            info = $"GetFocusedElement hr=0x{hr:X8}";
            return false;
        }

        var getPid = GetFn<GetCurrentProcessIdFn>(focused, ElementGetCurrentProcessId);
        var pidHr = getPid(focused, out var pid);
        if (pidHr != 0 || pid != foregroundProcessId)
        {
            _ = Marshal.Release(focused);
            info = $"pid-mismatch element={pid} foreground={foregroundProcessId}";
            return false;
        }

        element = focused;
        info = DescribeElement(focused);
        return true;
    }

    /// <summary>元素描述（ControlType / ClassName）：写进 placement.log 的采集 trace，便于排查。</summary>
    private static string DescribeElement(IntPtr element)
    {
        var controlType = -1;
        var className = "";
        try
        {
            _ = GetFn<GetCurrentControlTypeFn>(element, ElementGetCurrentControlType)
                .Invoke(element, out controlType);
        }
        catch
        {
            // 诊断失败不影响主流程
        }
        try
        {
            _ = GetFn<GetCurrentClassNameFn>(element, ElementGetCurrentClassName)
                .Invoke(element, out className);
        }
        catch
        {
            // 诊断失败不影响主流程
        }
        return $"ct={controlType} class='{className}'";
    }

    private static bool TryElementFromHandle(IntPtr uia, IntPtr hwnd, out IntPtr element, out string why)
    {
        element = IntPtr.Zero;
        why = "";
        var elementFromHandle = GetFn<ElementFromHandleFn>(uia, UiAutomationElementFromHandle);
        var hr = elementFromHandle(uia, hwnd, out var root);
        if (hr != 0 || root == IntPtr.Zero)
        {
            why = $"ElementFromHandle hr=0x{hr:X8}";
            return false;
        }
        element = root;
        return true;
    }

    /// <summary>窗口内「应用自身键盘焦点」的探查结果，决定要不要退回旧兜底。</summary>
    private enum WindowFocusProbe
    {
        /// <summary>找到持焦元素并取到了它的（或子树里的）TextPattern caret。</summary>
        FoundCaret,

        /// <summary>找到持焦元素，但它（及子树）没有可读 caret（焦点在按钮/非文本控件）。</summary>
        FoundNoCaret,

        /// <summary>没有元素报告键盘焦点，或该条件查询不可用：调用方应退回旧兜底。</summary>
        Unavailable,
    }

    /// <summary>
    /// 在目标窗口子树里找 HasKeyboardFocus=TRUE 的元素并取它的 caret。
    /// 为什么不用全局 <see cref="TryFocusedElement"/>：面板 Show 会抢走全局焦点，全局焦点元素
    /// 此时属于本进程（pid 不匹配）；而 UIA 的 HasKeyboardFocus 属性反映**目标应用内部的焦点**
    ///（Chrome / Electron 在失去前台后仍报 TRUE，2026-11-13 实机探针验证），能找回用户正在编辑的元素。
    /// </summary>
    private static WindowFocusProbe TryWindowKeyboardFocusedCaret(IntPtr uia, IntPtr root,
        out Win32Input.PhysicalRect caret, out string note)
    {
        caret = default;
        if (!TryFindFirstWithCondition(uia, root, HasKeyboardFocusPropertyId, out var focused, out var findNote))
        {
            note = "kbd-focus:" + findNote;
            return WindowFocusProbe.Unavailable;
        }

        try
        {
            var info = DescribeElement(focused);
            if (TryElementCaretOrAnchor(focused, out caret, out var directNote))
            {
                note = $"kbd-focus({info}) -> {directNote}";
                return WindowFocusProbe.FoundCaret;
            }

            // 持焦元素本身不带 TextPattern 时，问它子树里的第一个 TextPattern（持焦的可能是文档容器）
            if (TryFirstTextElement(uia, focused, out var textElement, out var textNote))
            {
                try
                {
                    if (TryElementCaretOrAnchor(textElement, out caret, out var caretNote))
                    {
                        note = $"kbd-focus-subtree({info}) -> {caretNote}";
                        return WindowFocusProbe.FoundCaret;
                    }
                    note = $"kbd-focus({info}) no-caret:{directNote}; subtree no-caret:{caretNote}";
                }
                finally
                {
                    _ = Marshal.Release(textElement);
                }
            }
            else
            {
                note = $"kbd-focus({info}) no-caret:{directNote}; no-text:{textNote}";
            }
            return WindowFocusProbe.FoundNoCaret;
        }
        finally
        {
            _ = Marshal.Release(focused);
        }
    }

    /// <summary>FindFirst(Descendants) 一个「布尔属性 = TRUE」的后代；找不到/条件不可用返回 false。</summary>
    private static bool TryFindFirstWithCondition(IntPtr uia, IntPtr root, int propertyId,
        out IntPtr element, out string why)
    {
        element = IntPtr.Zero;
        why = "";
        var condition = IntPtr.Zero;
        try
        {
            // 注意：CreatePropertyCondition 属于 IUIAutomation（uia），不是元素对象
            var createCondition = GetFn<CreatePropertyConditionFn>(uia, UiAutomationCreatePropertyCondition);
            var value = new VARIANT { vt = VtBool, data1 = new IntPtr(-1) };
            var hrCreate = createCondition(uia, propertyId, value, out condition);
            if (hrCreate != 0 || condition == IntPtr.Zero)
            {
                why = $"CreatePropertyCondition({propertyId}) hr=0x{hrCreate:X8}";
                return false;
            }

            var findFirst = GetFn<FindFirstFn>(root, ElementFindFirst);
            var hrFind = findFirst(root, ScopeDescendants, condition, out var found);
            if (hrFind != 0 || found == IntPtr.Zero)
            {
                // S_OK + NULL = 正常「没找到」；有错误码却给了指针的伪异常组合防御性释放
                if (found != IntPtr.Zero) _ = Marshal.Release(found);
                why = hrFind == 0 ? "not-found" : $"FindFirst({propertyId}) hr=0x{hrFind:X8}";
                return false;
            }
            element = found;
            return true;
        }
        finally
        {
            if (condition != IntPtr.Zero) _ = Marshal.Release(condition);
        }
    }

    private static bool TryFirstTextElement(IntPtr uia, IntPtr root, out IntPtr element, out string why) =>
        TryFindFirstWithCondition(uia, root, IsTextPatternAvailablePropertyId, out element, out why);

    /// <summary>取元素的 TextPattern 选区第一个 range 的包围矩形。</summary>
    private static bool TryElementCaret(IntPtr element, out Win32Input.PhysicalRect caret, out string why)
    {
        caret = default;
        why = "";

        var getPattern = GetFn<GetCurrentPatternFn>(element, ElementGetCurrentPattern);
        var hrPattern = getPattern(element, TextPatternId, out var pattern);
        if (hrPattern != 0 || pattern == IntPtr.Zero)
        {
            why = $"GetCurrentPattern(TextPattern) hr=0x{hrPattern:X8} ptr=0x{pattern:X}";
            return false;
        }

        try
        {
            var getSelection = GetFn<GetSelectionFn>(pattern, TextPatternGetSelection);
            var hrSelection = getSelection(pattern, out var ranges);
            if (hrSelection != 0 || ranges == IntPtr.Zero)
            {
                why = $"GetSelection hr=0x{hrSelection:X8}";
                return false;
            }

            try
            {
                var getCount = GetFn<GetRangeCountFn>(ranges, RangeArrayGetLength);
                var hrCount = getCount(ranges, out var count);
                if (hrCount != 0 || count <= 0)
                {
                    why = $"range-count={count} hr=0x{hrCount:X8}";
                    return false;
                }

                var getRange = GetFn<GetRangeElementFn>(ranges, RangeArrayGetElement);
                var hrRange = getRange(ranges, 0, out var range);
                if (hrRange != 0 || range == IntPtr.Zero)
                {
                    why = $"GetElement(0) hr=0x{hrRange:X8}";
                    return false;
                }

                try
                {
                    if (!TryReadRect(range, out caret, out why)) return false;
                    why = $"rect=({caret.Left},{caret.Top},{caret.Width},{caret.Height})";
                    return true;
                }
                finally
                {
                    _ = Marshal.Release(range);
                }
            }
            finally
            {
                _ = Marshal.Release(ranges);
            }
        }
        finally
        {
            _ = Marshal.Release(pattern);
        }
    }

    /// <summary>
    /// 取元素的 TextPattern caret；拿不到时按两种「输入框退化」处理（2026-11-14）：
    /// ① 拿到的是整行/整框宽矩形（Chrome 空输入框）且落在元素矩形内 → 零宽锚点（Left/Top）；
    /// ② 完全拿不到选区（WinUI TextBox 返回 0 个 range，如 Win11 记事本「查找」框），但元素是
    ///    焦点 Edit、矩形高度像一行 → 退化为元素左上角锚点。
    /// 两者都仍由采集链的三层校验把关。
    /// </summary>
    private static bool TryElementCaretOrAnchor(IntPtr element, out Win32Input.PhysicalRect caret,
        out string why)
    {
        if (!TryElementCaret(element, out caret, out var note))
        {
            if (TryElementBoxAnchor(element, out caret, out var boxNote))
            {
                why = $"{note} -> {boxNote}";
                return true;
            }
            why = note;
            return false;
        }

        if (CaretValidation.SizeLooksLikeCaret(caret.Width, caret.Height))
        {
            why = note;
            return true;
        }

        if (TryGetElementBounds(element, out var bounds)
            && CaretValidation.LooksLikeLineBoxAnchor(caret, bounds))
        {
            why = $"{note} -> line-anchor(left={caret.Left},top={caret.Top})";
            caret = new Win32Input.PhysicalRect(caret.Left, caret.Top, caret.Left, caret.Bottom);
            return true;
        }

        why = $"{note} -> reject:not-a-line-box";
        return false;
    }

    /// <summary>焦点 Edit 拿不到 caret 时的退路：用元素左上角当锚点（WinUI TextBox 的 0-range 场景）。</summary>
    private static bool TryElementBoxAnchor(IntPtr element, out Win32Input.PhysicalRect caret,
        out string why)
    {
        caret = default;
        if (TryGetElementControlType(element) != EditControlType)
        {
            why = "not-edit";
            return false;
        }
        if (!TryGetElementBounds(element, out var bounds))
        {
            why = "no-bounds";
            return false;
        }
        if (!CaretValidation.LooksLikeInputBoxAnchor(bounds))
        {
            why = $"bad-box {bounds.Width}x{bounds.Height}";
            return false;
        }
        why = $"element-box(Edit {bounds.Width}x{bounds.Height}) -> left={bounds.Left},top={bounds.Top}";
        caret = new Win32Input.PhysicalRect(bounds.Left, bounds.Top, bounds.Left, bounds.Bottom);
        return true;
    }

    private static int TryGetElementControlType(IntPtr element)
    {
        try
        {
            _ = GetFn<GetCurrentControlTypeFn>(element, ElementGetCurrentControlType)(element, out var ct);
            return ct;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>元素当前屏幕矩形（物理像素）；失败返回 false（诊断/宽选区锚点判定用）。</summary>
    private static bool TryGetElementBounds(IntPtr element, out Win32Input.PhysicalRect bounds)
    {
        bounds = default;
        try
        {
            var getBounds = GetFn<GetBoundingRectangleFn>(element, ElementGetCurrentBoundingRectangle);
            var hr = getBounds(element, out var r);
            if (hr != 0) return false;
            bounds = new Win32Input.PhysicalRect(r.Left, r.Top, r.Right, r.Bottom);
            return true;
        }
        catch
        {
            // 诊断/兜底失败不影响主流程
            return false;
        }
    }

    private static bool TryReadRect(IntPtr range, out Win32Input.PhysicalRect caret, out string why)
    {
        caret = default;
        why = "";
        var getRectangles = GetFn<GetBoundingRectanglesFn>(range, TextRangeGetBoundingRectangles);
        var hr = getRectangles(range, out var psa);
        if (hr != 0 || psa == IntPtr.Zero)
        {
            why = $"GetBoundingRectangles hr=0x{hr:X8}";
            return false;
        }

        try
        {
            var hrLb = SafeArrayGetLBound(psa, 1, out var lower);
            var hrUb = SafeArrayGetUBound(psa, 1, out var upper);
            if (hrLb != 0 || hrUb != 0 || upper < lower)
            {
                why = "bounds-failed";
                return false;
            }

            var count = upper - lower + 1;
            if (count < 4)
            {
                why = $"rect-count={count}";
                return false;
            }

            if (SafeArrayAccessData(psa, out var data) != 0 || data == IntPtr.Zero)
            {
                why = "SafeArrayAccessData-failed";
                return false;
            }
            var values = new double[count];
            try
            {
                Marshal.Copy(data, values, 0, count);
            }
            finally
            {
                _ = SafeArrayUnaccessData(psa);
            }

            var (x, y, w, h) = (values[0], values[1], values[2], values[3]);
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h))
            {
                why = "non-finite-rect";
                return false;
            }
            var left = (int)Math.Round(x);
            var top = (int)Math.Round(y);
            var width = (int)Math.Round(w);
            var height = (int)Math.Round(h);
            if (width < 0 || height < 0)
            {
                why = $"negative-size {width}x{height}";
                return false;
            }
            caret = new Win32Input.PhysicalRect(left, top, left + width, top + height);
            return true;
        }
        finally
        {
            _ = SafeArrayDestroy(psa);
        }
    }

    private static T GetFn<T>(IntPtr comObject, int vtableSlot) where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(comObject);
        var fn = Marshal.ReadIntPtr(vtable, vtableSlot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(fn);
    }
}
