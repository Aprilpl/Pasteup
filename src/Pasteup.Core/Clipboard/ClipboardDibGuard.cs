using System.Runtime.InteropServices;
using Pasteup.Core.Interop;

namespace Pasteup.Core.Clipboard;

/// <summary>剪贴板 CF_DIB 头预读：像素限额的解码前防线。
/// Clipboard.GetImage() 的 w×h×4 分配先于后台段的限额检查——超大声明尺寸的「DIB 炸弹」
/// 在 UI 线程就已分配；这里只锁头部 40 字节读声明尺寸，不做全量拷贝。</summary>
public static class ClipboardDibGuard
{
    /// <summary>预读当前剪贴板 CF_DIB 的声明尺寸。剪贴板打不开（他进程占用）/无 DIB/
    /// 头不全返回 null——调用方不得拦截，回退既有 GetImage 路径由后台段限额兜底。</summary>
    public static (int Width, int Height)? GetDimensions()
    {
        if (!NativeMethods.OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var h = NativeMethods.GetClipboardData(NativeMethods.CF_DIB);
            if (h == IntPtr.Zero) return null;
            if (NativeMethods.GlobalSize(h).ToUInt64() < 40) return null; // BITMAPINFOHEADER 都不全
            var p = NativeMethods.GlobalLock(h);
            if (p == IntPtr.Zero) return null;
            try
            {
                var header = new byte[40];
                Marshal.Copy(p, header, 0, 40);
                return ParseDimensions(header);
            }
            finally { _ = NativeMethods.GlobalUnlock(h); }
        }
        finally { NativeMethods.CloseClipboard(); }
    }

    /// <summary>BITMAPINFOHEADER（40 字节）解析，纯函数可单测：biWidth@4、biHeight@8
    ///（负值 = top-down 位图，高度取绝对值）。头不足 40 字节返回 null。</summary>
    public static (int Width, int Height)? ParseDimensions(ReadOnlySpan<byte> header)
    {
        if (header.Length < 40) return null;
        var width = BitConverter.ToInt32(header.Slice(4, 4));
        var rawHeight = BitConverter.ToInt32(header.Slice(8, 4));
        // biHeight = int.MinValue（0x80000000）时 Math.Abs 会抛 OverflowException，把 DIB 限额防线
        // 自己炸掉：钳为 int.MaxValue——语义等同「声明高度极大」，调用方的 50MP 判据必然拒收；
        // 其余负值（top-down 位图）保持取绝对值语义
        var height = rawHeight == int.MinValue ? int.MaxValue : Math.Abs(rawHeight);
        return (width, height);
    }
}
