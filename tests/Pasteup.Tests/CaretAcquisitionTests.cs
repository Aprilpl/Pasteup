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

    [Fact]
    public void Native_only_probe_chain_contains_exactly_native()
    {
        // 首落位（Show 之前，UI 线程）只做本进程调用：任何跨进程探针混进来都会重新引入
        // 「Show 前卡 UI」（复审问题1）。异步 UIA→MSAA 链在 UiaCaretProbeThread 内部构造。
        Assert.Collection(CaretAcquisition.NativeOnlyProbes,
            p => Assert.Equal(CaretSource.Native, p.Source));
    }
}
