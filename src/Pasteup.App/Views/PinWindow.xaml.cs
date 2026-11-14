using System.Windows;
using System.Windows.Input;
using Pasteup.App.Services;
using Pasteup.Core.Capture;

namespace Pasteup.App.Views;

public partial class PinWindow : Window
{
    private readonly int _naturalWidth;
    private readonly int _naturalHeight;
    private double _baseScaleX = 1.0; // 物理像素 → 本窗口 DIP（OnSourceInitialized 按目标显示器 DPI 确定）
    private bool _clickThrough;

    /// <summary>托盘「贴图列表」的缩略图源：直接复用已解码的显示源（截图链路为零解码冻结图），
    /// 不额外解码、不复制像素。</summary>
    public System.Windows.Media.ImageSource DisplaySource => Img.Source;

    /// <summary>选区原始物理像素尺寸（贴图窗口按显示器 DPI 折算后显示，菜单里报告原图尺寸）。</summary>
    public int NaturalWidth => _naturalWidth;

    public int NaturalHeight => _naturalHeight;

    public PinWindow(byte[] png, int physicalWidth, int physicalHeight, double opacity,
        System.Windows.Media.ImageSource? display = null)
    {
        InitializeComponent();
        // display = 截图链路的选区独立副本（已冻结的 WriteableBitmap，零解码直显；只含选区像素，
        // 不再引用整屏定格位图——2026-11-10 复审问题2）；无则按 PNG 解码（历史入口）
        Img.Source = display ?? ImageCodec.FromPng(png);
        Opacity = opacity;
        _naturalWidth = physicalWidth;
        _naturalHeight = physicalHeight;
        // 整面可拖 + 边缘 8 DIP 等比拉宽高（贴图非分层窗口，破坏纵横比会露黑边）；手动引擎，不依赖系统移动循环
        Services.WindowDragging.Attach(this, 8, lockAspect: true);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Monitors.Invalidate();
        Services.DisplayChangeHook.Attach(this);
        // 图像为物理像素，窗口 DIP 尺寸须按目标显示器 DPI 折算；构造期 PresentationSource 未建、
        // 可取到的 DPI 恒 96（老 bug：>100% 屏上贴图整档放大），HWND 就绪后按光标所在屏重算
        var cursor = Services.Win32Input.GetCursorPos();
        var monitor = Pasteup.Core.Capture.Monitors.ContainingPhysicalPoint(cursor.X, cursor.Y)
                      ?? Pasteup.Core.Capture.Monitors.Primary();
        _baseScaleX = 96.0 / (monitor?.Dpi ?? 96);
        Width = _naturalWidth * _baseScaleX;
        Height = _naturalHeight * _baseScaleX;
        PositionNearCursor(monitor);
    }

    private void PositionNearCursor(Pasteup.Core.Capture.MonitorRect? monitor)
    {
        var p = Services.Win32Input.GetCursorPos();
        var dpiScale = 96.0 / (monitor?.Dpi ?? 96);
        var dipX = p.X * dpiScale;
        var dipY = p.Y * dpiScale;
        Left = dipX - Math.Min(Width * 0.5, 60);
        Top = dipY - Math.Min(Height * 0.5, 60);
    }

    private void OnResetZoom(object sender, MouseButtonEventArgs e)
    {
        // 双击 = 复位到 100%（物理尺寸 × DIP 折算），等比还原高度
        var dipWidth = _naturalWidth * _baseScaleX;
        var ratio = dipWidth / Math.Max(1, Width);
        Width = dipWidth;
        Height *= ratio;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.F9:
                ToggleClickThrough();
                break;
        }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var delta = e.Delta > 0 ? 0.05 : -0.05;
            Opacity = Math.Clamp(Opacity + delta, 0.2, 1.0);
        }
        else
        {
            var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
            ZoomAroundCursor(factor);
        }
        e.Handled = true;
    }

    /// <summary>以光标为锚点缩放（光标下的图像点保持在光标下）。</summary>
    private void ZoomAroundCursor(double factor)
    {
        var pos = Mouse.GetPosition(this);
        var newWidth = Math.Max(40, Width * factor);
        var newHeight = Math.Max(30, Height * factor);
        var anchorRatioX = pos.X / Math.Max(1, Width);
        var anchorRatioY = pos.Y / Math.Max(1, Height);
        var newLeft = Left + pos.X - anchorRatioX * newWidth;
        var newTop = Top + pos.Y - anchorRatioY * newHeight;
        Width = newWidth;
        Height = newHeight;
        Left = newLeft;
        Top = newTop;
    }

    private void ToggleClickThrough()
    {
        _clickThrough = !_clickThrough;
        Services.Win32Input.SetClickThrough(this, _clickThrough);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 关闭时恢复穿透样式，避免窗口句柄复用问题（防御性）
        if (_clickThrough) Services.Win32Input.SetClickThrough(this, false);
    }

    private void OnMenuCopy(object sender, RoutedEventArgs e)
    {
        var bmp = (System.Windows.Media.Imaging.BitmapSource)Img.Source;
        Clipboard.SetImage(bmp);
    }

    private void OnMenuClose(object sender, RoutedEventArgs e) => Close();

    private void OnMenuToggleThrough(object sender, RoutedEventArgs e) => ToggleClickThrough();
}
