using Pasteup.App.Services;
using Xunit;

namespace Pasteup.Tests;

/// <summary>IngestLimits 纯函数：CF_HDROP 文件列表限额（条数 / 单路径字符数 / Σ UTF8 字节）的
/// 边界与跨限额路径。纯函数，不依赖 WPF/系统剪贴板。</summary>
public class IngestLimitsTests
{
    [Fact]
    public void Normal_two_path_file_list_is_accepted()
        => Assert.False(IngestLimits.FileListTooLarge([@"C:\a.txt", @"C:\b.txt"]));

    [Fact]
    public void Empty_list_is_not_flagged_size_wise()
        => Assert.False(IngestLimits.FileListTooLarge([])); // 空列表由调用方（ReadClipboardPayload）另行拒绝

    [Fact]
    public void Max_file_count_is_inclusive()
        => Assert.False(IngestLimits.FileListTooLarge(
            Enumerable.Repeat(@"C:\a.txt", IngestLimits.MaxFileCount).ToList()));

    [Fact]
    public void Count_over_max_is_rejected()
        => Assert.True(IngestLimits.FileListTooLarge(
            Enumerable.Repeat(@"C:\a.txt", IngestLimits.MaxFileCount + 1).ToList()));

    [Fact]
    public void Max_path_length_is_inclusive()
        => Assert.False(IngestLimits.FileListTooLarge([new string('a', IngestLimits.MaxFilePathChars)]));

    [Fact]
    public void Path_over_max_length_is_rejected()
        => Assert.True(IngestLimits.FileListTooLarge([new string('a', IngestLimits.MaxFilePathChars + 1)]));

    [Fact]
    public void Total_bytes_at_limit_is_accepted()
    {
        // 320 × 32K ASCII = 恰好 10MB：不超限（判定用 >，边界落在合法侧）；
        // 每条恰为单路径上限、条数 < 条数上限，故只有 Σ 字节限额起作用
        var files = Enumerable.Repeat(new string('a', IngestLimits.MaxFilePathChars), 320).ToList();
        Assert.Equal((long)IngestLimits.MaxTextBytes, files.Sum(f => (long)f.Length));
        Assert.False(IngestLimits.FileListTooLarge(files));
    }

    [Fact]
    public void Total_bytes_over_limit_is_rejected()
    {
        // 400 × 30000 = 12MB > 10MB：每条都短于单路径上限、条数也远小于条数上限，
        // 只能由 Σ UTF8 字节限额拒收
        var files = Enumerable.Repeat(new string('a', 30_000), 400).ToList();
        Assert.DoesNotContain(files, f => f.Length > IngestLimits.MaxFilePathChars);
        Assert.True(IngestLimits.FileListTooLarge(files));
    }

    [Fact]
    public void Two_6mb_paths_are_rejected()
    {
        // 审计给的示例形态：2 条各 6MB 的字符串——单条长度与 Σ 字节两道限额都超
        var files = new List<string> { new('a', 6 * 1024 * 1024), new('a', 6 * 1024 * 1024) };
        Assert.True(IngestLimits.FileListTooLarge(files));
    }

    [Fact]
    public void Total_bytes_use_utf8_not_char_count()
    {
        // 150 × 24000 个汉字 = 360 万字符（< 10M 字符，按字符数算会漏）+ 1080 万字节（> 10MB）：
        // 判定必须按 UTF-8 字节口径（与 TextTooLarge 一致）
        var files = Enumerable.Repeat(new string('中', 24_000), 150).ToList();
        Assert.True(files.Sum(f => f.Length) < IngestLimits.MaxTextBytes);
        Assert.True(IngestLimits.FileListTooLarge(files));
    }
}
