using System.Windows;
using Pasteup.App.Services;
using Pasteup.App.Views;
using Pasteup.Core.Capture;

namespace Pasteup.App.Services;

/// <summary>截图流程：热键 → 覆盖层选区 → GDI 捕获 → 贴图 + 入历史。v1 在鼠标所在显示器全屏选区。</summary>
public static class ScreenshotService
{
    /// <summary>截图进行中标志（重入守卫，2026-11-10 复审其他#4）。热键/托盘双击会连发两次
    /// BeginCapture：旧实现每次都 new 一层全屏覆盖层（4K 每层定格 ≈33MB + 一次全屏 BitBlt、ctor
    /// ~273ms），Esc 一次只关最上层、下面还盖着一层。只在 UI 线程访问（热键经 Dispatcher.BeginInvoke、
    /// 托盘菜单 Click 均在 UI 线程），无需原子操作。</summary>
    private static bool _captureInFlight;

    /// <summary>preferredMonitor 为 null 时取鼠标所在显示器（v1 语义，全部现调用方）。
    /// 已有截图在进行时直接返回（重入守卫）；覆盖层关闭后（Esc 取消 / 确认后自关 / 空选区 /
    /// 系统关闭）自动复位，失败分支（无显示器 / 构造或 Show 抛异常）在 finally 里复位。</summary>
    public static void BeginCapture(MonitorRect? preferredMonitor = null)
    {
        if (_captureInFlight) return;
        _captureInFlight = true;
        var handedOff = false; // 覆盖层已创建并挂上 Closed 复位：此后标志随窗口生命周期走
        try
        {
            var monitor = preferredMonitor ?? CursorMonitor() ?? Monitors.Primary();
            if (monitor is null) return;
            var overlay = new ScreenshotOverlayWindow(monitor);
            overlay.RegionConfirmed += (shot, display) => OnCaptured(shot, display);
            // 所有关闭路径都从 Closed 复位；不能只看 RegionConfirmed——Esc 取消/空选区没有回调
            overlay.Closed += (_, _) => _captureInFlight = false;
            overlay.Show();
            handedOff = true;
        }
        finally
        {
            // 失败分支必须复位，否则守卫永久锁死后续截图（Show/ctor 抛异常仍按原语义向外抛）
            if (!handedOff) _captureInFlight = false;
        }
    }

    private static void OnCaptured(CapturedImage shot, System.Windows.Media.Imaging.BitmapSource display)
    {
        var app = App.Current;
        var id = Guid.NewGuid();
        var file = app.Store.SaveImagePng(id, shot.Png);
        var entry = Core.History.ClipboardEntry.FromImage(file, shot.Width, shot.Height, shot.Png.Length, "Pasteup 截图");
        entry.Id = id;
        app.History.Add(entry);
        app.Pins.Open(shot.Png, shot.Width, shot.Height, app.Settings.PinDefaultOpacity, display);
        // 入库预缩略图（640 宽）后台生成：4K 选区缩放 + PNG 编码在 UI 线程可达 10–30ms，不能压在
        // 「松开→贴图可见」链路上。同步时序保持原样（主图已落盘、条目已入库、贴图已打开），
        // 缩略图只是列表解码优化，失败一律忽略（ThumbConverter 缺缩略图自动回退原图）。
        // display 已 Freeze（ScreenshotOverlayWindow.ConfirmSelection），store 只写文件、可跨线程重入。
        var store = app.Store;
        _ = Task.Run(() =>
        {
            // 与 ClipboardIngestService.BuildImage 同口径：仅比缩略图规格宽的图才生成（不放大、不重复落盘）
            if (display.PixelWidth <= Core.History.HistoryStore.ThumbnailWidth) return;
            try
            {
                store.SaveThumbnailPng(id,
                    ImageCodec.RenderThumbnailPng(display, Core.History.HistoryStore.ThumbnailWidth));
            }
            catch (Exception)
            {
                // best-effort：生成/落盘失败只剩「下一次滚动多解一次原图」的成本
            }
        });
    }

    private static MonitorRect? CursorMonitor()
    {
        var p = Win32Input.GetCursorPos();
        return Monitors.ContainingPhysicalPoint(p.X, p.Y);
    }
}
