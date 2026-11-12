using System.Drawing;
using System.Drawing.Imaging;
using Pasteup.Core.Interop;

namespace Pasteup.Core.Capture;

public sealed record CapturedImage(byte[] Png, int Width, int Height);

/// <summary>GDI BitBlt 屏幕捕获（物理像素坐标）。含 CAPTUREBLT 以抓取分层窗口；不含鼠标光标。</summary>
public static class ScreenCapture
{
    /// <summary>
    /// BitBlt 指定物理区域为 GDI+ Bitmap（调用方负责 Dispose）。
    /// 截图覆盖层用它一次定格整屏，确认选区时从定格裁剪（Encode）——所见即所得，
    /// 且不对活屏二次 BitBlt（原路径确认时重截，Close 与 BitBlt 存在遮罩自身入镜的竞态）。
    /// </summary>
    public static Bitmap CaptureRegionBitmap(CaptureRect rect)
    {
        if (rect.IsEmpty) throw new ArgumentException("空区域不可捕获", nameof(rect));
        var w = rect.Width;
        var h = rect.Height;

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDc = NativeMethods.CreateCompatibleDC(screenDc);
        IntPtr bmp = NativeMethods.CreateCompatibleBitmap(screenDc, w, h);
        IntPtr old = NativeMethods.SelectObject(memDc, bmp);
        try
        {
            NativeMethods.BitBlt(memDc, 0, 0, w, h, screenDc, rect.X, rect.Y,
                NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
            return Image.FromHbitmap(bmp);
        }
        finally
        {
            NativeMethods.SelectObject(memDc, old);
            NativeMethods.DeleteObject(bmp);
            NativeMethods.DeleteDC(memDc);
            if (NativeMethods.ReleaseDC(IntPtr.Zero, screenDc) == 0)
            {
                // 释放失败无补救路径（屏幕 DC 由系统管理），注明以过 CA1806
            }
        }
    }

    /// <summary>
    /// 把位图的子矩形编码为 PNG（裁剪走 GDI+ Clone 纯内存拷贝；crop 恰为整幅时不克隆）。
    /// 这是截图链路唯一一次编码——PNG 是历史落盘产物，绕不开；显示路径不经此处。
    /// </summary>
    public static CapturedImage Encode(Bitmap bitmap, Rectangle crop)
    {
        var whole = crop.X == 0 && crop.Y == 0 && crop.Width == bitmap.Width && crop.Height == bitmap.Height;
        Bitmap? clone = whole ? null : bitmap.Clone(crop, bitmap.PixelFormat);
        try
        {
            var target = clone ?? bitmap;
            using var ms = new MemoryStream();
            target.Save(ms, ImageFormat.Png);
            return new CapturedImage(ms.ToArray(), target.Width, target.Height);
        }
        finally
        {
            clone?.Dispose();
        }
    }
}
