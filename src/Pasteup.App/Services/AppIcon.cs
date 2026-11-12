using System.IO;
using System.Windows;

namespace Pasteup.App.Services;

/// <summary>应用图标装载：WPF Resource 里的 Assets/app.ico → GDI Icon（托盘用）；
/// 资源缺失或解码失败一律回退 TrayIconPainter 代码绘制，托盘图标永远拿得到。</summary>
public static class AppIcon
{
    /// <summary>从 .ico 流取最接近 targetSize 的帧；返回的 Icon 自持句柄，由调用方 Dispose。</summary>
    public static System.Drawing.Icon FromIcoStream(Stream stream, int targetSize = 32) =>
        new(stream, new System.Drawing.Size(targetSize, targetSize));

    public static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
            if (resource is null) return TrayIconPainter.PaintIcon();
            using var stream = resource.Stream;
            return FromIcoStream(stream);
        }
        catch (Exception ex)
        {
            CrashLog.Write("tray-icon", ex, "嵌入 app.ico 加载失败，回退代码绘制");
            return TrayIconPainter.PaintIcon();
        }
    }
}
