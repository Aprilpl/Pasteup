using System.Runtime.InteropServices;

namespace Pasteup.App.Services;

/// <summary>
/// UI Automation TextPattern 取 caret（2026-11-10）：Chrome / Edge / Electron / VS Code 等自绘光标应用
/// **没有** native caret（GetGUIThreadInfo 全 0）、MSAA OBJID_CARET 也返回 S_FALSE，但 UIA 的
/// `FocusedElement → TextPattern.GetSelection() → GetBoundingRectangles()` 能给出 caret 矩形
/// （实测 Chrome：行首 (225,528,1,38)、行尾 (571,528,1,38)，与 Ditto 面板位置 (left+width, top+20) 吻合）。
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
    /// 主路径：UIA 焦点元素；兜底：前台窗口后代里第一个 IsTextPatternAvailable 元素
    /// （焦点元素偶尔是 IME/宿主窗口，拿不到 TextPattern）。
    /// </summary>
    internal static bool TryReadCaret(IntPtr uia, IntPtr foregroundWindow, int foregroundProcessId,
        out Win32Input.PhysicalRect caret, out string why)
    {
        caret = default;
        var focusedWhy = "";
        var windowWhy = "";

        if (TryFocusedElement(uia, foregroundProcessId, out var focused, out var focusedInfo))
        {
            try
            {
                if (TryElementCaret(focused, out caret, out var note))
                {
                    why = "focused:" + note;
                    return true;
                }
                focusedWhy = focusedInfo + " -> " + note;
            }
            finally
            {
                _ = Marshal.Release(focused);
            }
        }
        else
        {
            focusedWhy = focusedInfo;
        }

        if (foregroundWindow != IntPtr.Zero)
        {
            if (TryElementFromHandle(uia, foregroundWindow, out var root, out var rootNote))
            {
                try
                {
                    if (TryFirstTextElement(uia, root, out var textElement, out var findNote))
                    {
                        try
                        {
                            if (TryElementCaret(textElement, out caret, out var note))
                            {
                                why = "window:" + note;
                                return true;
                            }
                            findNote = note;
                        }
                        finally
                        {
                            _ = Marshal.Release(textElement);
                        }
                    }
                    windowWhy = findNote;
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

    private static bool TryFirstTextElement(IntPtr uia, IntPtr root, out IntPtr element, out string why)
    {
        element = IntPtr.Zero;
        why = "";

        // 条件：IsTextPatternAvailable = TRUE（VARIANT_BOOL true = -1）
        var condition = IntPtr.Zero;
        try
        {
            // 注意：CreatePropertyCondition 属于 IUIAutomation（uia），不是元素对象
            var createCondition = GetFn<CreatePropertyConditionFn>(uia, UiAutomationCreatePropertyCondition);
            var value = new VARIANT { vt = VtBool, data1 = new IntPtr(-1) };
            var hrCreate = createCondition(uia, IsTextPatternAvailablePropertyId, value, out condition);
            if (hrCreate != 0 || condition == IntPtr.Zero)
            {
                why = $"CreatePropertyCondition hr=0x{hrCreate:X8}";
                return false;
            }

            var findFirst = GetFn<FindFirstFn>(root, ElementFindFirst);
            var hrFind = findFirst(root, ScopeDescendants, condition, out var found);
            if (hrFind != 0 || found == IntPtr.Zero)
            {
                why = $"FindFirst(TextPattern) hr=0x{hrFind:X8}";
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
