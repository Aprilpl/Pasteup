using Pasteup.Core.Text;
using Xunit;

namespace Pasteup.Tests;

public class TextUtilTests
{
    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://example.com/a?b=c")]
    [InlineData("HTTPS://EXAMPLE.COM")]
    [InlineData("ftp://files.example.com")]
    [InlineData("file:///C:/tmp/a.txt")]
    [InlineData("www.example.com")]
    public void Single_url_detected(string text) => Assert.True(TextUtil.IsSingleUrl(text));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello world")]
    [InlineData("https://a.com 和别的")]
    [InlineData("line1\nhttps://a.com")]
    [InlineData("not-a-url")]
    [InlineData("example.com/path")] // 无 scheme 前缀且非 www，不算
    public void Non_url_rejected(string text) => Assert.False(TextUtil.IsSingleUrl(text));

    [Theory]
    [InlineData("  a  b  ", 100, "a b")]
    [InlineData("a\r\nb\tc", 100, "a b c")]
    [InlineData("", 100, "")]
    public void MakePreview_collapses_whitespace(string text, int max, string expected)
        => Assert.Equal(expected, TextUtil.MakePreview(text, max));

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(200)]
    public void MakePreview_truncates_with_ellipsis(int max)
    {
        var result = TextUtil.MakePreview(new string('字', max + 10), max);
        Assert.Equal(max, result.Length);
        Assert.EndsWith("…", result);
    }

    [Fact]
    public void MakePreview_keeps_short_text_verbatim()
        => Assert.Equal("短文本", TextUtil.MakePreview("短文本", 100));

    [Fact]
    public void MakePreview_stops_early_on_huge_text()
    {
        // 20 万字符长文：流式折叠攒够 max 即收手，不再全量 Split（语义与全量折叠一致）
        var big = string.Join(' ', Enumerable.Range(0, 100_000).Select(_ => "词"));
        var preview = TextUtil.MakePreview(big, 200);
        Assert.Equal(200, preview.Length);
        Assert.EndsWith("…", preview);
        Assert.StartsWith("词 词", preview);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(-5, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "2 KB")]
    [InlineData(1024 * 1024, "1.0 MB")]
    [InlineData((long)(2.4 * 1024 * 1024), "2.4 MB")]
    [InlineData(1024L * 1024 * 1024, "1.00 GB")]
    [InlineData(3L * 1024 * 1024 * 1024, "3.00 GB")]
    public void FormatSize_units(long bytes, string expected)
        => Assert.Equal(expected, TextUtil.FormatSize(bytes));
}

public class TimeFormatTests
{
    [Fact]
    public void Today_shows_hhmm()
        // 「现在 -5 分钟」会在午夜附近跨天（23:59 的 5 分钟前 = 昨天）→ 改用今天正午，永远同日
        => Assert.Matches(@"^\d{2}:\d{2}$", TimeFormat.Format(DateTime.Today.AddHours(12).AddMinutes(-5)));

    [Fact]
    public void Yesterday_has_marker()
        => Assert.StartsWith("昨天", TimeFormat.Format(DateTime.Now.AddDays(-1)));

    [Fact]
    public void This_week_shows_weekday()
    {
        var d = DateTime.Now.AddDays(-3);
        var text = TimeFormat.Format(d);
        Assert.DoesNotContain("月", text);
        Assert.Contains(':', text);
    }

    [Fact]
    public void Earlier_shows_full_date()
    {
        var d = DateTime.Now.AddDays(-20);
        Assert.Contains("月", TimeFormat.Format(d));
        Assert.Contains("日", TimeFormat.Format(d));
    }

    [Fact]
    public void Boundary_six_days_ago_is_this_week()
        => Assert.DoesNotContain("月", TimeFormat.Format(DateTime.Now.AddDays(-6)));

    [Fact]
    public void Boundary_seven_days_ago_is_earlier()
        => Assert.Contains("月", TimeFormat.Format(DateTime.Now.AddDays(-7)));
}
