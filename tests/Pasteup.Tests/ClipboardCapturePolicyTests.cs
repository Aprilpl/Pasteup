using System.IO;
using System.Runtime.InteropServices;
using Pasteup.App.Services;
using Xunit;

namespace Pasteup.Tests;

/// <summary>入库退订策略（2026-11-10 C1-A）：纯 lambda 委托，不碰真剪贴板。
/// 覆盖 DWORD 格式（int/uint/long/short/byte[]/MemoryStream 的 0 与非 0、空/短流/不可识别类型）、
/// presence-only 格式、组合、未知格式与检查异常（可用性优先不跳过）。</summary>
public class ClipboardCapturePolicyTests
{
    private const string FmtHistory = ClipboardCapturePolicy.CanIncludeInClipboardHistory;
    private const string FmtCloud = ClipboardCapturePolicy.CanUploadToCloudClipboard;
    private const string FmtMonitor = ClipboardCapturePolicy.ExcludeClipboardContentFromMonitorProcessing;
    private const string FmtViewer = ClipboardCapturePolicy.ClipboardViewerIgnore;

    /// <summary>模拟剪贴板：<paramref name="present"/> 里的格式视为存在；值从 <paramref name="values"/> 取，缺省 null。</summary>
    private static bool Evaluate(string[] present, Dictionary<string, object?>? values = null)
        => ClipboardCapturePolicy.ShouldSkip(
            format => Array.IndexOf(present, format) >= 0,
            format => values is not null && values.TryGetValue(format, out var value) ? value : null);

    private static bool EvaluateOne(string format, object? value)
        => Evaluate([format], new Dictionary<string, object?> { [format] = value });

    private static MemoryStream StreamOf(params byte[] bytes) => new(bytes);

    // ---- 协议常量（写错名字 = 功能静默失效，回归锁定） ----

    [Fact]
    public void Format_constants_match_windows_conventions()
    {
        Assert.Equal("CanIncludeInClipboardHistory", ClipboardCapturePolicy.CanIncludeInClipboardHistory);
        Assert.Equal("CanUploadToCloudClipboard", ClipboardCapturePolicy.CanUploadToCloudClipboard);
        Assert.Equal("ExcludeClipboardContentFromMonitorProcessing",
            ClipboardCapturePolicy.ExcludeClipboardContentFromMonitorProcessing);
        Assert.Equal("Clipboard Viewer Ignore", ClipboardCapturePolicy.ClipboardViewerIgnore);
    }

    // ---- 无标记 / 未知格式 ----

    [Fact]
    public void No_markers_no_skip()
        => Assert.False(Evaluate([]));

    [Fact]
    public void Unknown_format_does_not_affect()
        => Assert.False(Evaluate(["SomeOtherFormat"]));

    // ---- 历史 DWORD 格式 ----

    [Fact]
    public void History_zero_skips()
        => Assert.True(EvaluateOne(FmtHistory, 0));

    [Fact]
    public void History_nonzero_allows()
        => Assert.False(EvaluateOne(FmtHistory, 1));

    [Fact]
    public void History_zero_across_dword_types_skips()
    {
        Assert.True(EvaluateOne(FmtHistory, 0));
        Assert.True(EvaluateOne(FmtHistory, 0u));
        Assert.True(EvaluateOne(FmtHistory, 0L));
        Assert.True(EvaluateOne(FmtHistory, (short)0));
        Assert.True(EvaluateOne(FmtHistory, new byte[] { 0, 0, 0, 0 }));
        Assert.True(EvaluateOne(FmtHistory, StreamOf(0, 0, 0, 0)));
    }

    [Fact]
    public void History_nonzero_across_dword_types_allows()
    {
        Assert.False(EvaluateOne(FmtHistory, 1));
        Assert.False(EvaluateOne(FmtHistory, 1u));
        Assert.False(EvaluateOne(FmtHistory, 1L));
        Assert.False(EvaluateOne(FmtHistory, (short)1));
        Assert.False(EvaluateOne(FmtHistory, new byte[] { 1, 0, 0, 0 }));
        Assert.False(EvaluateOne(FmtHistory, StreamOf(1, 0, 0, 0)));
        Assert.False(EvaluateOne(FmtHistory, StreamOf(0, 0, 0, 1))); // 小端：0x01000000 非 0
    }

    [Fact]
    public void History_null_value_skips_conservatively()
        => Assert.True(EvaluateOne(FmtHistory, null)); // 标记存在但值读不到 → 按 0（隐私优先）

    [Fact]
    public void History_unrecognized_type_skips_conservatively()
    {
        Assert.True(EvaluateOne(FmtHistory, "0"));       // 字符串不可识别
        Assert.True(EvaluateOne(FmtHistory, new object())); // 任意对象
        Assert.True(EvaluateOne(FmtHistory, new byte[] { 0, 0, 0 })); // 短 byte[]（<4）
        Assert.True(EvaluateOne(FmtHistory, Array.Empty<byte>()));
    }

    [Fact]
    public void Byte_array_longer_than_4_uses_first_little_endian_dword()
    {
        Assert.True(EvaluateOne(FmtHistory, new byte[] { 0, 0, 0, 0, 9, 9 }));  // 前 4 字节 = 0 → 跳过
        Assert.False(EvaluateOne(FmtHistory, new byte[] { 1, 0, 0, 0, 9, 9 })); // 前 4 字节 = 1 → 放行
    }

    // ---- MemoryStream（WPF 跨进程 OLE 读自定义格式的常见返回类型） ----

    [Fact]
    public void MemoryStream_zero_skips()
        => Assert.True(EvaluateOne(FmtHistory, StreamOf(0, 0, 0, 0)));

    [Fact]
    public void MemoryStream_nonzero_allows()
    {
        Assert.False(EvaluateOne(FmtHistory, StreamOf(1, 0, 0, 0)));
        Assert.False(EvaluateOne(FmtHistory, StreamOf(0, 0, 0, 1)));
    }

    [Fact]
    public void MemoryStream_empty_or_short_skips_conservatively()
    {
        Assert.True(EvaluateOne(FmtHistory, new MemoryStream()));                    // 空流
        Assert.True(EvaluateOne(FmtHistory, StreamOf(0, 0)));                        // 短流（<4）
        Assert.True(EvaluateOne(FmtHistory, StreamOf(0, 0, 0)));                     // 短流（3 字节）
        var disposed = StreamOf(1, 0, 0, 0);
        disposed.Dispose();
        Assert.True(EvaluateOne(FmtHistory, disposed)); // 已释放流：读不到值 → 跳过
    }

    [Fact]
    public void MemoryStream_reads_from_head_regardless_of_position()
    {
        // 长度 6、Position=5：若从当前位置读会短流→跳过；从头读前 4 字节 = 1 → 放行
        var stream = StreamOf(1, 0, 0, 0, 9, 9);
        stream.Position = 5;
        Assert.False(EvaluateOne(FmtHistory, stream));
        Assert.Equal(5, stream.Position); // 读完复位原位置（不干扰其它潜在读取方）
    }

    // ---- 云剪贴板 DWORD 格式 ----

    [Fact]
    public void Cloud_zero_skips_and_nonzero_allows()
    {
        Assert.True(EvaluateOne(FmtCloud, 0));
        Assert.False(EvaluateOne(FmtCloud, 1));
        Assert.True(EvaluateOne(FmtCloud, StreamOf(0, 0, 0, 0)));
        Assert.False(EvaluateOne(FmtCloud, StreamOf(1, 0, 0, 0)));
    }

    // ---- presence-only 格式 ----

    [Theory]
    [InlineData(FmtMonitor)]
    [InlineData(FmtViewer)]
    public void Presence_only_formats_skip_when_present(string format)
    {
        Assert.True(EvaluateOne(format, null)); // 值无意义（甚至读不到）
        Assert.True(EvaluateOne(format, 0));
        Assert.True(EvaluateOne(format, 1));
        // presence-only 不应调用 getData：getData 抛异常也照样跳过
        Assert.True(ClipboardCapturePolicy.ShouldSkip(
            f => f == format, _ => throw new COMException("剪贴板被占用")));
    }

    // ---- 组合 ----

    [Fact]
    public void Any_marker_wins_over_allow_value()
    {
        // 历史/云都明确允许，但监控排除标记存在 → 仍跳过
        Assert.True(Evaluate([FmtHistory, FmtCloud, FmtMonitor],
            new Dictionary<string, object?> { [FmtHistory] = 1, [FmtCloud] = 1 }));
        // 只有允许值、无 presence-only 标记 → 不跳过
        Assert.False(Evaluate([FmtHistory, FmtCloud],
            new Dictionary<string, object?> { [FmtHistory] = 1, [FmtCloud] = 1 }));
    }

    // ---- 检查异常：可用性优先（该格式视为不跳过，异常不外抛） ----

    [Fact]
    public void Contains_exception_does_not_skip()
        => Assert.False(ClipboardCapturePolicy.ShouldSkip(
            _ => throw new COMException("剪贴板被占用"), _ => 0));

    [Fact]
    public void GetData_exception_does_not_skip()
        => Assert.False(ClipboardCapturePolicy.ShouldSkip(
            f => f == FmtHistory, _ => throw new COMException("剪贴板被占用")));

    [Fact]
    public void Exception_for_one_format_does_not_abort_others()
    {
        // 历史/云格式查询抛异常，但监控排除标记能正常回答 → 依旧跳过
        Assert.True(ClipboardCapturePolicy.ShouldSkip(
            f => f == FmtMonitor ? true : throw new COMException("剪贴板被占用"),
            _ => null));
    }
}
