using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Pasteup.Core.Capture;

namespace Pasteup.App.Views;

/// <summary>
/// 单显示器截图覆盖层（结构见 XAML 注释）。开窗时 BitBlt 一次定格整屏；确认选区时从定格
/// 裁剪（所见即所得）。RegionConfirmed 回调（PNG 落盘用 + 已冻结的选区独立副本，贴图窗免解码）。
/// </summary>
public sealed partial class ScreenshotOverlayWindow : Window
{
    private readonly MonitorRect _monitor;
    private readonly double _dipScale; // 物理像素 → DIP
    private readonly System.Drawing.Bitmap _frozen; // 定格整屏（GDI+），Closed 时 Dispose
    private readonly BitmapSource _frozenView; // 定格显示层（冻结，遮罩显示用），确认时只作裁剪取样源
    private Point _start;
    private bool _dragging;
    private Rect _selection; // DIP

    public event Action<CapturedImage, BitmapSource>? RegionConfirmed;

    public ScreenshotOverlayWindow(MonitorRect monitor)
    {
        InitializeComponent();
        _monitor = monitor;
        _dipScale = 96.0 / monitor.Dpi;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.X * _dipScale;
        Top = monitor.Y * _dipScale;
        Width = monitor.Width * _dipScale;
        Height = monitor.Height * _dipScale;

        // 定格一次：GDI+ 位图留作确认时裁剪，显示层 LockBits 直转（无整屏 PNG 编码→解码往返）
        _frozen = ScreenCapture.CaptureRegionBitmap(
            new CaptureRect(monitor.X, monitor.Y, monitor.Width, monitor.Height));
        _frozenView = Services.ImageCodec.FromGdiBitmap(_frozen);
        Shot.Source = _frozenView;
        Closed += (_, _) => _frozen.Dispose();

        MouseMove += (_, _) => HintTag.Visibility = Visibility.Hidden;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        _dragging = true;
        _selection = new Rect(_start, new Size(0, 0));
        UpdateMasks(_selection);
        SelBorder.Visibility = Visibility.Visible;
        SizeTag.Visibility = Visibility.Visible;
        CaptureMouse();
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        _selection = new Rect(
            Math.Min(_start.X, p.X), Math.Min(_start.Y, p.Y),
            Math.Abs(p.X - _start.X), Math.Abs(p.Y - _start.Y));
        UpdateMasks(_selection);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        ConfirmSelection(_selection);
    }

    /// <summary>POC/冒烟驱动口：按 DIP 选区走与真实松开完全相同的确认路径（定格裁剪→编码→回调）。</summary>
    public void PocConfirmSelection(Rect sel) => ConfirmSelection(sel);

    private void ConfirmSelection(Rect sel)
    {
        var phys = new CaptureRect(
            _monitor.X + (int)Math.Round(sel.X / _dipScale),
            _monitor.Y + (int)Math.Round(sel.Y / _dipScale),
            (int)Math.Round(sel.Width / _dipScale),
            (int)Math.Round(sel.Height / _dipScale));
        var region = CaptureMath.NormalizeDrag(phys.X, phys.Y, phys.X + phys.Width, phys.Y + phys.Height, minSize: 4);
        if (region.IsEmpty)
        {
            Close();
            return;
        }

        // 从定格裁剪（编码须在 Close 之前：Close → Closed → _frozen.Dispose）。
        //
        // 显示源必须是**独立副本**，不能是 CroppedBitmap 视图：CroppedBitmap 只是整屏
        // _frozenView 上的一个"视口"，自身不持有像素；贴图窗（PinWindow）会把它持有到关闭，
        // 于是每张贴图都钉住整屏定格位图（4K≈33MB/张，贴图数无上限且贴图可见时工作集不修剪）
        // → 内存只增不减。WriteableBitmap(BitmapSource) 构造期把选区像素 memcpy 进自己的
        // 后台缓冲（拷贝量 = 选区像素 ×4B：1/4 个 4K 选区≈8MB、200×100≈80KB，通常 1-3ms），
        // Freeze 后转为普通冻结 BitmapSource，与 _frozenView 再无引用关系；贴图窗只留这张小图。
        var crop = new System.Drawing.Rectangle(region.X - _monitor.X, region.Y - _monitor.Y, region.Width, region.Height);
        var shot = ScreenCapture.Encode(_frozen, crop);
        var cropped = new CroppedBitmap(_frozenView,
            new System.Windows.Int32Rect(crop.X, crop.Y, crop.Width, crop.Height));
        cropped.Freeze();
        var display = new WriteableBitmap(cropped); // 视图 → 拷贝（materialize 选区像素）
        display.Freeze();
        Close();
        RegionConfirmed?.Invoke(shot, display);
    }

    /// <summary>POC 驱动口：外部按帧注入选区（DIP），复用与真实拖拽完全相同的渲染路径。</summary>
    public void PocUpdateSelection(Rect sel)
    {
        _dragging = true;
        UpdateMasks(sel);
    }

    /// <summary>四块遮罩围出选区挖孔；空选区时整屏一块遮罩。</summary>
    private void UpdateMasks(Rect sel)
    {
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        if (sel.Width <= 0 || sel.Height <= 0)
        {
            Place(MaskTop, 0, 0, w, h);
            Place(MaskLeft, 0, 0, 0, 0);
            Place(MaskRight, 0, 0, 0, 0);
            Place(MaskBottom, 0, 0, 0, 0);
            SelBorder.Visibility = Visibility.Hidden;
            SizeTag.Visibility = Visibility.Hidden;
            return;
        }
        Place(MaskTop, 0, 0, w, sel.Y);
        Place(MaskLeft, 0, sel.Y, sel.X, sel.Height);
        Place(MaskRight, sel.X + sel.Width, sel.Y, w - sel.X - sel.Width, sel.Height);
        Place(MaskBottom, 0, sel.Y + sel.Height, w, h - sel.Y - sel.Height);

        Canvas.SetLeft(SelBorder, sel.X);
        Canvas.SetTop(SelBorder, sel.Y);
        SelBorder.Width = sel.Width;
        SelBorder.Height = sel.Height;

        SizeText.Text = $"{(int)Math.Round(sel.Width / _dipScale)} × {(int)Math.Round(sel.Height / _dipScale)}";
        SizeTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(SizeTag, sel.X);
        Canvas.SetTop(SizeTag, sel.Y - SizeTag.DesiredSize.Height - 6);
        if (Canvas.GetTop(SizeTag) < 0) Canvas.SetTop(SizeTag, sel.Y + 6);
    }

    private static void Place(Rectangle r, double x, double y, double w, double h)
    {
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
        r.Width = Math.Max(0, w);
        r.Height = Math.Max(0, h);
    }
}
