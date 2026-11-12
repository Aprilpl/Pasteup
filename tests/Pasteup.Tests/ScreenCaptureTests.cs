using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Media.Imaging;
using Pasteup.App.Services;
using Pasteup.Core.Capture;
using Xunit;

namespace Pasteup.Tests;

/// <summary>
/// 截图定格-裁剪链路（2026-11-05 C6）：Encode 是链路唯一编码点，FromGdiBitmap 是显示直转点，
/// 两者像素口径必须与旧「编码→解码」往返逐字节一致（PNG 无损，可精确断言）。
/// </summary>
public class ScreenCaptureTests
{
    /// <summary>100×80 四象限图：TL 红 / TR 柠绿 / BL 蓝 / BR 品红（偶数尺寸保证象限整除）。</summary>
    private static Bitmap QuadBitmap()
    {
        var bmp = new Bitmap(100, 80, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.FillRectangle(Brushes.Red, 0, 0, 50, 40);
        g.FillRectangle(Brushes.Lime, 50, 0, 50, 40);
        g.FillRectangle(Brushes.Blue, 0, 40, 50, 40);
        g.FillRectangle(Brushes.Magenta, 50, 40, 50, 40);
        return bmp;
    }

    private static byte[] Bgra(BitmapSource src, int x, int y)
    {
        var pixels = new byte[4];
        src.CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixels, 4, 0);
        return pixels;
    }

    private static void AssertBgra(byte[] px, byte b, byte g, byte r)
    {
        Assert.Equal((b, g, r, (byte)255), (px[0], px[1], px[2], px[3]));
    }

    [Fact]
    public void Encode_subrectangle_crops_pixels_exactly()
    {
        using var quad = QuadBitmap();
        var shot = ScreenCapture.Encode(quad, new Rectangle(50, 0, 50, 40)); // TR 象限
        Assert.Equal((50, 40), (shot.Width, shot.Height));

        var decoded = ImageCodec.FromPng(shot.Png);
        Assert.Equal((50, 40), (decoded.PixelWidth, decoded.PixelHeight));
        AssertBgra(Bgra(decoded, 1, 1), 0, 255, 0);   // 柠绿
        AssertBgra(Bgra(decoded, 48, 38), 0, 255, 0); // 象限内另一角仍是柠绿（整块同色）
    }

    [Fact]
    public void Encode_whole_bitmap_skips_clone_and_keeps_pixels()
    {
        using var quad = QuadBitmap();
        var shot = ScreenCapture.Encode(quad, new Rectangle(0, 0, 100, 80));
        Assert.Equal((100, 80), (shot.Width, shot.Height));

        var decoded = ImageCodec.FromPng(shot.Png);
        AssertBgra(Bgra(decoded, 1, 1), 0, 0, 255);      // TL 红
        AssertBgra(Bgra(decoded, 51, 41), 255, 0, 255);  // BR 品红
    }

    [Fact]
    public void FromGdiBitmap_converts_frozen_without_png_roundtrip()
    {
        using var quad = QuadBitmap();
        var view = ImageCodec.FromGdiBitmap(quad);
        Assert.True(view.IsFrozen);
        Assert.Equal((100, 80), (view.PixelWidth, view.PixelHeight));
        AssertBgra(Bgra(view, 1, 1), 0, 0, 255);     // TL 红
        AssertBgra(Bgra(view, 51, 1), 0, 255, 0);    // TR 柠绿
        AssertBgra(Bgra(view, 1, 41), 255, 0, 0);    // BL 蓝
        AssertBgra(Bgra(view, 51, 41), 255, 0, 255); // BR 品红
    }

    /// <summary>确认路径的显示源（贴图窗直显）与落盘 PNG 是同一像素：遮罩所见 = 贴图所得。</summary>
    [Fact]
    public void CroppedBitmap_display_source_matches_encode_crop()
    {
        using var quad = QuadBitmap();
        var frozen = ImageCodec.FromGdiBitmap(quad);
        var display = new CroppedBitmap(frozen, new System.Windows.Int32Rect(50, 40, 50, 40)); // BR 象限
        display.Freeze();
        Assert.True(display.IsFrozen);
        Assert.Equal((50, 40), (display.PixelWidth, display.PixelHeight));

        var shot = ScreenCapture.Encode(quad, new Rectangle(50, 40, 50, 40));
        var decoded = ImageCodec.FromPng(shot.Png);
        AssertBgra(Bgra(display, 1, 1), Bgra(decoded, 1, 1)[0], Bgra(decoded, 1, 1)[1], Bgra(decoded, 1, 1)[2]);
    }
}
