using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pasteup.App.Services;
using Xunit;

namespace Pasteup.Tests;

/// <summary>剪贴板图像吞入的两个修复点："PNG" 格式提取 + GDI 全 0 alpha 归一化。</summary>
public class ImageIngestTests
{
    private static BitmapSource MakeBitmap(PixelFormat format, int w, int h, byte[] pixels)
        => BitmapSource.Create(w, h, 96, 96, format, null, pixels, w * 4);

    private static byte[] PixelsOf(BitmapSource src)
    {
        var bytes = new byte[src.PixelWidth * src.PixelHeight * 4];
        src.CopyPixels(bytes, src.PixelWidth * 4, 0);
        return bytes;
    }

    // ---- ForceOpaqueIfAllAlphaZero ----

    [Fact]
    public void Gdi_zero_alpha_with_content_becomes_opaque()
    {
        // 2×3 BGRA：GDI 截图典型形态——RGB 有内容、alpha 字节全是未初始化的 0
        var pixels = new byte[6 * 4];
        for (var i = 0; i < 6; i++)
        {
            pixels[i * 4] = 200;      // B
            pixels[i * 4 + 1] = 100;  // G
            pixels[i * 4 + 2] = 50;   // R
        }
        var src = MakeBitmap(PixelFormats.Bgra32, 2, 3, pixels);

        var fixedSource = ImageCodec.ForceOpaqueIfAllAlphaZero(src);

        var result = PixelsOf(fixedSource);
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(255, result[i * 4 + 3]);
            Assert.Equal(200, result[i * 4]);      // RGB 不动
            Assert.Equal(100, result[i * 4 + 1]);
            Assert.Equal(50, result[i * 4 + 2]);
        }
    }

    [Fact]
    public void Pbgra32_zero_alpha_with_content_also_fixed()
    {
        var pixels = new byte[2 * 4];
        pixels[0] = pixels[5] = 255; // 两个有色像素
        var src = MakeBitmap(PixelFormats.Pbgra32, 2, 1, pixels);

        var fixedSource = ImageCodec.ForceOpaqueIfAllAlphaZero(src);

        Assert.All(PixelsOf(fixedSource).Where((_, i) => i % 4 == 3), a => Assert.Equal(255, a));
    }

    [Fact]
    public void Fully_blank_image_left_untouched()
    {
        // RGB 与 alpha 全 0：真空白（已预乘归零），置不透明只会得到黑图，保持原样
        var src = MakeBitmap(PixelFormats.Bgra32, 2, 2, new byte[2 * 2 * 4]);

        var fixedSource = ImageCodec.ForceOpaqueIfAllAlphaZero(src);

        Assert.All(PixelsOf(fixedSource), b => Assert.Equal(0, b));
    }

    [Fact]
    public void Real_transparency_left_untouched()
    {
        // 部分 alpha 非 0：真透明图（圆角边缘等），不能动
        var pixels = new byte[2 * 4];
        pixels[3] = 128;  // 半透明
        pixels[7] = 255;  // 不透明
        var src = MakeBitmap(PixelFormats.Bgra32, 2, 1, pixels);

        var fixedSource = ImageCodec.ForceOpaqueIfAllAlphaZero(src);

        var result = PixelsOf(fixedSource);
        Assert.Equal(128, result[3]);
        Assert.Equal(255, result[7]);
    }

    [Fact]
    public void Non_alpha_format_left_untouched()
    {
        var pixels = new byte[2 * 4]; // Bgr32 无 alpha 通道
        var src = MakeBitmap(PixelFormats.Bgr32, 2, 1, pixels);

        Assert.Same(src, ImageCodec.ForceOpaqueIfAllAlphaZero(src));
    }

    [Fact]
    public void Fixed_image_survives_png_roundtrip_opaque()
    {
        var pixels = new byte[3 * 4];
        for (var i = 0; i < 3; i++) pixels[i * 4 + 1] = 255; // 绿色内容 + alpha 0
        var src = MakeBitmap(PixelFormats.Bgra32, 3, 1, pixels);

        var png = ImageCodec.ToPng(ImageCodec.ForceOpaqueIfAllAlphaZero(src));
        var back = (BitmapSource)ImageCodec.FromPng(png);

        var result = PixelsOf(back);
        Assert.All(result.Where((_, i) => i % 4 == 3), a => Assert.Equal(255, a));
    }

    // ---- PngBytesFromClipboardData ----

    private static readonly byte[] PngHeader = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void Png_extracted_from_memory_stream()
    {
        var png = PngHeader.Concat(new byte[] { 1, 2, 3 }).ToArray();
        using var ms = new MemoryStream(png);
        Assert.Equal(png, ImageCodec.PngBytesFromClipboardData(ms));
    }

    [Fact]
    public void Png_extracted_from_byte_array()
    {
        var png = PngHeader.Concat(new byte[] { 9 }).ToArray();
        Assert.Equal(png, ImageCodec.PngBytesFromClipboardData(png));
    }

    [Theory]
    [InlineData(new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 })]          // 非 PNG 魔数
    [InlineData(new byte[] { 0x89, 0x50, 0x4E })]                  // 截断
    public void Non_png_bytes_rejected(byte[] data)
        => Assert.Null(ImageCodec.PngBytesFromClipboardData(data));

    [Theory]
    [InlineData("not png")]
    [InlineData("")]
    public void Unexpected_types_rejected(object data)
        => Assert.Null(ImageCodec.PngBytesFromClipboardData(data));

    [Fact]
    public void Null_rejected()
        => Assert.Null(ImageCodec.PngBytesFromClipboardData(null));

    // ---- 入库限额（2026-11-02 安全加固）：解码炸弹在解码前拒收 ----

    [Fact]
    public void TryReadPngDimensions_parses_real_png()
    {
        var png = ImageCodec.ToPng(MakeBitmap(PixelFormats.Bgr32, 3, 2, new byte[3 * 2 * 4]));
        Assert.Equal((3u, 2u), ImageCodec.TryReadPngDimensions(png));
    }

    [Fact]
    public void TryReadPngDimensions_null_for_short_data()
        => Assert.Null(ImageCodec.TryReadPngDimensions([1, 2, 3]));

    [Fact]
    public void TryReadPngDimensions_null_for_wrong_magic()
        => Assert.Null(ImageCodec.TryReadPngDimensions(new byte[24]));

    /// <summary>手工构造「IHDR 声明超大尺寸」的炸弹头：守卫必须在 BitmapImage 解码前抛 InvalidOperationException。</summary>
    private static byte[] DimensionBomb(uint width, uint height)
    {
        var png = new byte[33]; // 8 魔数 + 4 长度 + "IHDR" + 4W + 4H + 9 字垃圾
        PngHeader.CopyTo(png);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), height);
        return png;
    }

    [Fact]
    public void FromPng_rejects_dimension_bomb_before_decode()
        => Assert.Throws<InvalidOperationException>(
            () => ImageCodec.FromPng(DimensionBomb(100_000, 100_000)));

    [Fact]
    public void FromPng_accepts_normal_dimensions()
        => Assert.NotNull(ImageCodec.FromPng(
            ImageCodec.ToPng(MakeBitmap(PixelFormats.Bgr32, 4, 4, new byte[4 * 4 * 4]))));

    [Fact]
    public void Oversize_png_bytes_rejected_from_clipboard_data()
    {
        var bytes = new byte[ImageCodec.MaxPngBytes + 1];
        PngHeader.CopyTo(bytes);
        Assert.Null(ImageCodec.PngBytesFromClipboardData(bytes));
    }

    [Fact]
    public void Clipboard_text_size_limit_boundary()
    {
        Assert.False(IngestLimits.TextTooLarge(new string('a', IngestLimits.MaxTextBytes)));
        Assert.True(IngestLimits.TextTooLarge(new string('a', IngestLimits.MaxTextBytes + 1)));
    }
}
