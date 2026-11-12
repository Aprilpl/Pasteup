using Pasteup.App.Services;
using Xunit;

namespace Pasteup.Tests;

/// <summary>托盘图标产出约束：H.NotifyIcon 的 IconSource 只认 URI 来源的 BitmapImage/BitmapFrame，
/// 代码绘制位图走该路径会抛 "ImageSource type ... is not supported" / UriFormatException（2026-10-27 踩坑），
/// 故托盘图标必须以 GDI Icon 形式经 TaskbarIcon.Icon 交付。</summary>
public class TrayIconTests
{
    [Fact]
    public void PaintIcon_returns_owned_32px_icon()
    {
        using var icon = TrayIconPainter.PaintIcon();
        Assert.Equal(32, icon.Size.Width);
        Assert.Equal(32, icon.Size.Height);
        Assert.NotEqual(IntPtr.Zero, icon.Handle);
    }
}
