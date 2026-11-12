using Pasteup.App.Services;
using Pasteup.Core.Clipboard;
using Xunit;

namespace Pasteup.Tests;

public class ClipboardDibGuardTests
{
    private static byte[] Header(int width, int height, ushort bitCount = 32)
    {
        var b = new byte[40];
        BitConverter.GetBytes(40).CopyTo(b, 0);         // biSize
        BitConverter.GetBytes(width).CopyTo(b, 4);     // biWidth
        BitConverter.GetBytes(height).CopyTo(b, 8);    // biHeight（负 = top-down）
        BitConverter.GetBytes((short)1).CopyTo(b, 12); // biPlanes
        BitConverter.GetBytes(bitCount).CopyTo(b, 14); // biBitCount
        return b;
    }

    [Fact]
    public void Parses_bottom_up_dimensions()
        => Assert.Equal((640, 480), ClipboardDibGuard.ParseDimensions(Header(640, 480)));

    [Fact]
    public void Negative_height_is_top_down_and_taken_as_absolute()
        => Assert.Equal((800, 600), ClipboardDibGuard.ParseDimensions(Header(800, -600)));

    [Fact]
    public void Oversized_declared_dimensions_are_readable_for_limit_check()
        => Assert.Equal((50000, 50000), ClipboardDibGuard.ParseDimensions(Header(50000, 50000)));

    [Fact]
    public void Min_value_height_is_clamped_to_int_max_instead_of_overflowing()
    {
        // 安全审计 C5：biHeight = int.MinValue（0x80000000）时 Math.Abs 抛 OverflowException，
        // 把 DIB 限额防线自己炸掉。修复后钳为 int.MaxValue（按「声明高度极大」处理），
        // 宽度正常值不受影响，上层 50MP 判据必然拒收。
        var dims = ClipboardDibGuard.ParseDimensions(Header(800, int.MinValue));
        Assert.True(dims.HasValue);
        var d = dims.GetValueOrDefault();
        Assert.Equal(800, d.Width);
        Assert.Equal(int.MaxValue, d.Height);
        Assert.True((long)d.Width * d.Height > ImageCodec.MaxImagePixels);
    }

    [Theory]
    [InlineData(39)]
    [InlineData(0)]
    public void Too_short_header_returns_null(int len)
        => Assert.Null(ClipboardDibGuard.ParseDimensions(new byte[len]));
}
