using Pasteup.App.Services;
using Xunit;
using PhysicalRect = Pasteup.App.Services.Win32Input.PhysicalRect;

namespace Pasteup.Tests;

/// <summary>
/// caret 采集与校验（2026-11-10 面板锚点三阶段落地）。
/// 覆盖：客户区→屏幕坐标换算（阶段 0 的真 bug）、尺寸/焦点控件/显示器三层校验、
/// 采集链顺序与逐级回退、探针异常隔离、默认探针顺序。
/// 实机行为（native/UIA/MSAA 各自在哪些应用命中）由 --poc=caret 冒烟覆盖。
/// </summary>
public class CaretAcquisitionTests
{
    private static readonly PhysicalRect Focus = new(100, 100, 900, 800);

    private static CaretProbeContext Context(PhysicalRect? focus = null, bool onMonitor = true) =>
        new(new IntPtr(0x1234), focus ?? Focus, (_, _) => onMonitor);

    private sealed class FakeProbe : ICaretProbe
    {
        private readonly Func<IntPtr, (bool ok, PhysicalRect rect, string why)> _impl;
        public int Calls { get; private set; }

        public FakeProbe(CaretSource source, Func<IntPtr, (bool ok, PhysicalRect rect, string why)> impl)
        {
            Source = source;
            _impl = impl;
        }

        public CaretSource Source { get; }

        public bool TryGet(IntPtr foregroundWindow, out PhysicalRect caret, out string why)
        {
            Calls++;
            var (ok, rect, note) = _impl(foregroundWindow);
            caret = rect;
            why = note;
            return ok;
        }
    }

    // ---- 阶段 0：客户区 → 屏幕换算 ----

    [Fact]
    public void Translate_converts_client_rect_to_screen()
    {
        // 记事本实测：rcCaret=(28,28,29,62)（客户区）+ 客户区原点 (276,365) → 屏幕 (304,393,305,427)
        var screen = CaretValidation.Translate(new PhysicalRect(28, 28, 29, 62), 276, 365);
        Assert.Equal(new PhysicalRect(304, 393, 305, 427), screen);
    }

    [Fact]
    public void Client_rect_without_translation_is_rejected_outside_focus()
    {
        // 回归：旧实现把客户区矩形直接当屏幕坐标 → 窗口不在屏幕零点时必然被「焦点控件内」挡掉
        var clientRect = new PhysicalRect(28, 28, 29, 62);
        Assert.False(CaretValidation.InsideFocus(clientRect, Focus));
        Assert.True(CaretValidation.InsideFocus(CaretValidation.Translate(clientRect, 276, 365), Focus));
    }

    // ---- 尺寸校验 ----

    [Theory]
    [InlineData(1, 38, true)]   // 细竖线（Chrome UIA 实测 1×38）
    [InlineData(3, 52, true)]   // Sublime 实测 3×52
    [InlineData(0, 20, true)]   // 部分应用报 0 宽
    [InlineData(8, 300, true)]
    [InlineData(1, 1, false)]   // (0,0,1,1) 假 caret
    [InlineData(0, 0, false)]
    [InlineData(9, 38, false)]  // 太宽 → 整块控件
    [InlineData(1, 7, false)]   // 太矮
    [InlineData(1, 301, false)] // 太高 → 整窗
    [InlineData(-1, 38, false)]
    public void Size_looks_like_caret(int width, int height, bool expected) =>
        Assert.Equal(expected, CaretValidation.SizeLooksLikeCaret(width, height));

    // ---- 焦点控件校验 ----

    [Fact]
    public void Inside_focus_accepts_rect_inside_and_tolerance_edge()
    {
        Assert.True(CaretValidation.InsideFocus(new PhysicalRect(300, 300, 301, 340), Focus));
        Assert.True(CaretValidation.InsideFocus(new PhysicalRect(98, 300, 99, 340), Focus)); // 左越界 2 = 容差内
    }

    [Fact]
    public void Inside_focus_rejects_far_outside()
    {
        Assert.False(CaretValidation.InsideFocus(new PhysicalRect(20, 300, 21, 340), Focus));
        Assert.False(CaretValidation.InsideFocus(new PhysicalRect(300, 300, 950, 340), Focus));
    }

    // ---- 宽选区锚点判据（2026-11-14：Chrome 空输入框/整行选区）----

    [Fact]
    public void Line_box_anchor_accepts_selection_equal_to_focused_input()
    {
        // 实测 B 站搜索框（Chrome UIA）：空输入框的 TextPattern 选区矩形 == 元素自身矩形 721×35
        var element = new PhysicalRect(1678, 251, 2399, 286);
        Assert.True(CaretValidation.LooksLikeLineBoxAnchor(element, element));
    }

    [Fact]
    public void Line_box_anchor_accepts_line_selection_inside_document_element()
    {
        // 实测 Electron/DSH：文档元素内的一行输入区 2228×63（矩形落在元素矩形内）
        var element = new PhysicalRect(0, 212, 3842, 2078);
        Assert.True(CaretValidation.LooksLikeLineBoxAnchor(new PhysicalRect(1043, 1868, 3271, 1931), element));
    }

    [Fact]
    public void Line_box_anchor_rejects_thin_caret()
    {
        // 细光标不该走锚点降级（正常 caret 仍由尺寸校验直接放行）
        var element = new PhysicalRect(70, 357, 1343, 461);
        Assert.False(CaretValidation.LooksLikeLineBoxAnchor(new PhysicalRect(100, 400, 101, 448), element));
    }

    [Fact]
    public void Line_box_anchor_rejects_whole_window_rect()
    {
        // 整窗/整页矩形（高 > 300）不能被当 caret 锚点——这是旧「假 caret 拉角落」防线的延续
        var element = new PhysicalRect(0, 0, 3840, 2076);
        Assert.False(CaretValidation.LooksLikeLineBoxAnchor(new PhysicalRect(0, 0, 3840, 2076), element));
    }

    [Fact]
    public void Line_box_anchor_rejects_rect_outside_element_bounds()
    {
        var element = new PhysicalRect(100, 100, 500, 140);
        Assert.False(CaretValidation.LooksLikeLineBoxAnchor(new PhysicalRect(90, 100, 510, 140), element));
    }

    [Fact]
    public void Line_box_anchor_tolerates_two_pixel_edge_overflow()
    {
        var element = new PhysicalRect(100, 100, 500, 140);
        Assert.True(CaretValidation.LooksLikeLineBoxAnchor(new PhysicalRect(98, 100, 502, 140), element));
    }

    // ---- 焦点输入框左上角锚点判据（2026-11-14：WinUI TextBox 返回 0 个 range）----

    [Fact]
    public void Input_box_anchor_accepts_notepad_find_box()
    {
        // 实测 Win11 记事本 Ctrl+F「查找」框：TextBox (1277,402)-(2397,458)，TextPattern 无 range
        Assert.True(CaretValidation.LooksLikeInputBoxAnchor(new PhysicalRect(1277, 402, 2397, 458)));
    }

    [Fact]
    public void Input_box_anchor_rejects_full_window()
    {
        Assert.False(CaretValidation.LooksLikeInputBoxAnchor(new PhysicalRect(0, 0, 3840, 2076)));
    }

    [Fact]
    public void Input_box_anchor_rejects_too_small_or_too_thin()
    {
        Assert.False(CaretValidation.LooksLikeInputBoxAnchor(new PhysicalRect(100, 100, 104, 130))); // 宽 4
        Assert.False(CaretValidation.LooksLikeInputBoxAnchor(new PhysicalRect(100, 100, 300, 105))); // 高 5
    }

    // ---- 采集链 ----

    [Fact]
    public void First_valid_probe_wins_and_later_probes_are_not_called()
    {
        var native = new FakeProbe(CaretSource.Native, _ => (true, new PhysicalRect(300, 300, 301, 340), "ok"));
        var uia = new FakeProbe(CaretSource.Uia, _ => (true, new PhysicalRect(400, 400, 401, 440), "ok"));
        var hit = CaretAcquisition.TryAcquire(Context(), new ICaretProbe[] { native, uia }, out var trace);

        Assert.NotNull(hit);
        Assert.Equal(CaretSource.Native, hit!.Value.Source);
        Assert.Equal(0, uia.Calls);
        Assert.Contains("native:ok", trace);
    }

    [Fact]
    public void Bad_size_falls_through_to_next_source()
    {
        var native = new FakeProbe(CaretSource.Native, _ => (true, new PhysicalRect(300, 300, 301, 301), "fake"));
        var uia = new FakeProbe(CaretSource.Uia, _ => (true, new PhysicalRect(300, 300, 301, 340), "ok"));
        var hit = CaretAcquisition.TryAcquire(Context(), new ICaretProbe[] { native, uia }, out var trace);

        Assert.Equal(CaretSource.Uia, hit!.Value.Source);
        Assert.Contains("reject:size", trace);
    }

    [Fact]
    public void Outside_focus_falls_through_to_next_source()
    {
        var native = new FakeProbe(CaretSource.Native, _ => (true, new PhysicalRect(10, 10, 11, 40), "far"));
        var msaa = new FakeProbe(CaretSource.Msaa, _ => (true, new PhysicalRect(300, 300, 301, 340), "ok"));
        var hit = CaretAcquisition.TryAcquire(Context(), new ICaretProbe[] { native, msaa }, out var trace);

        Assert.Equal(CaretSource.Msaa, hit!.Value.Source);
        Assert.Contains("reject:outside-focus", trace);
    }

    [Fact]
    public void Off_monitor_center_falls_through()
    {
        var uia = new FakeProbe(CaretSource.Uia, _ => (true, new PhysicalRect(300, 300, 301, 340), "ok"));
        var native = new FakeProbe(CaretSource.Native, _ => (false, default, "no-caret"));
        var hit = CaretAcquisition.TryAcquire(Context(onMonitor: false),
            new ICaretProbe[] { uia, native }, out var trace);

        Assert.Null(hit);
        Assert.Contains("reject:off-monitor", trace);
    }

    [Fact]
    public void No_focus_rect_skips_containment_check()
    {
        var native = new FakeProbe(CaretSource.Native, _ => (true, new PhysicalRect(10, 10, 11, 40), "ok"));
        var context = new CaretProbeContext(new IntPtr(0x1234), null, (_, _) => true);
        var hit = CaretAcquisition.TryAcquire(context, new ICaretProbe[] { native }, out _);
        Assert.Equal(CaretSource.Native, hit!.Value.Source);
    }

    [Fact]
    public void All_failing_returns_null_and_trace_covers_every_source()
    {
        var probes = new ICaretProbe[]
        {
            new FakeProbe(CaretSource.Native, _ => (false, default, "no-caret-hwnd")),
            new FakeProbe(CaretSource.Uia, _ => (false, default, "timeout-150ms")),
            new FakeProbe(CaretSource.Msaa, _ => (false, default, "AOFW hr=0x80004005")),
        };
        var hit = CaretAcquisition.TryAcquire(Context(), probes, out var trace);

        Assert.Null(hit);
        Assert.Contains("native:no-caret-hwnd", trace);
        Assert.Contains("uia:timeout-150ms", trace);
        Assert.Contains("msaa:AOFW", trace);
    }

    [Fact]
    public void Throwing_probe_is_isolated_and_next_source_still_works()
    {
        var boom = new FakeProbe(CaretSource.Uia, _ => throw new InvalidOperationException("com boom"));
        var native = new FakeProbe(CaretSource.Native, _ => (true, new PhysicalRect(300, 300, 301, 340), "ok"));
        var hit = CaretAcquisition.TryAcquire(Context(), new ICaretProbe[] { boom, native }, out var trace);

        Assert.Equal(CaretSource.Native, hit!.Value.Source);
        Assert.Contains("exception:InvalidOperationException", trace);
    }

    [Fact]
    public void Default_probe_order_is_native_then_uia_then_msaa()
    {
        Assert.Collection(CaretAcquisition.DefaultProbes,
            p => Assert.Equal(CaretSource.Native, p.Source),
            p => Assert.Equal(CaretSource.Uia, p.Source),
            p => Assert.Equal(CaretSource.Msaa, p.Source));
    }

    // ---- 采样校验（采集链与 Show 前焦点快照共用）----

    [Fact]
    public void Validate_accepts_caret_inside_focus_on_monitor()
    {
        var hit = CaretAcquisition.Validate(Context(), CaretSource.Uia, new PhysicalRect(300, 300, 301, 340),
            "focused(...) -> rect=(300,300,1,40)", out var reject);

        Assert.NotNull(hit);
        Assert.Equal(CaretSource.Uia, hit!.Value.Source);
        Assert.Contains("focused", hit.Value.Detail);
        Assert.Equal("", reject);
    }

    [Theory]
    [InlineData(300, 300, 320, 340)]   // 宽 20 → 整块控件/选区，不是细光标
    [InlineData(300, 300, 301, 301)]   // 高 1 → 假 caret
    [InlineData(300, 300, 309, 340)]   // 宽 9 → 超细光标上限
    public void Validate_rejects_bad_size(int l, int t, int r, int b)
    {
        var hit = CaretAcquisition.Validate(Context(), CaretSource.Uia, new PhysicalRect(l, t, r, b),
            "probe", out var reject);

        Assert.Null(hit);
        Assert.StartsWith("size", reject);
    }

    [Fact]
    public void Validate_rejects_outside_focus()
    {
        var hit = CaretAcquisition.Validate(Context(), CaretSource.Uia, new PhysicalRect(10, 300, 11, 340),
            "probe", out var reject);

        Assert.Null(hit);
        Assert.StartsWith("outside-focus", reject);
    }

    [Fact]
    public void Validate_rejects_off_monitor()
    {
        var hit = CaretAcquisition.Validate(Context(onMonitor: false), CaretSource.Native,
            new PhysicalRect(300, 300, 301, 340), "probe", out var reject);

        Assert.Null(hit);
        Assert.StartsWith("off-monitor", reject);
    }

    [Fact]
    public void Success_trace_includes_probe_detail_for_diagnosis()
    {
        // 2026-11-13：成功也要把探针 why 带进 trace——placement.log 只记 trace，没有它就无法区分
        // UIA 是「全局焦点元素」还是「窗口内 HasKeyboardFocus 元素」命中的（Chrome 固定落位排查时
        // 正是靠外部探针才看清旧代码走的是「窗口第一个 TextPattern=地址栏」）。
        var uia = new FakeProbe(CaretSource.Uia,
            _ => (true, new PhysicalRect(300, 300, 301, 340), "window:kbd-focus(ct=50004) -> rect=(300,300,1,40)"));
        var hit = CaretAcquisition.TryAcquire(Context(), new ICaretProbe[] { uia }, out var trace);

        Assert.NotNull(hit);
        Assert.Contains("uia:ok:window:kbd-focus", trace);
        // CaretHit.Detail 与 trace 同源：下游无需再解析 trace 字符串
        Assert.Equal("window:kbd-focus(ct=50004) -> rect=(300,300,1,40)", hit!.Value.Detail);
    }

    [Fact]
    public void Success_trace_keeps_plain_ok_when_probe_has_no_detail()
    {
        var native = new FakeProbe(CaretSource.Native, _ => (true, new PhysicalRect(300, 300, 301, 340), ""));
        var hit = CaretAcquisition.TryAcquire(Context(), new ICaretProbe[] { native }, out var trace);

        Assert.NotNull(hit);
        Assert.Contains("native:ok", trace);
        Assert.DoesNotContain("native:ok:", trace);
    }

    [Fact]
    public void Native_only_probe_chain_contains_exactly_native()
    {
        // 首落位（Show 之前，UI 线程）只做本进程调用：任何跨进程探针混进来都会重新引入
        // 「Show 前卡 UI」（复审问题1）。异步 UIA→MSAA 链在 UiaCaretProbeThread 内部构造。
        Assert.Collection(CaretAcquisition.NativeOnlyProbes,
            p => Assert.Equal(CaretSource.Native, p.Source));
    }
}
