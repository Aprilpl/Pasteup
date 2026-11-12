using System.Windows;
using System.Windows.Input;

namespace Pasteup.App.Services;

/// <summary>
/// 无边框窗口的手动拖动与边缘缩放。不依赖 DragMove / WM_NCHITTEST 系统循环：
/// 实测部分输入路径（注入式输入、某些数位板/触摸驱动）无法驱动 DefWindowProc 的移动/缩放模态循环，
/// 导致「事件到了窗口却拖不动」。本类只用 WPF 鼠标事件 + GetCursorPos 物理增量直接改窗口矩形，
/// 对任何输入路径一致生效。核心矩形推导为纯函数（ApplyDrag）供单测。
/// </summary>
public static class WindowDragging
{
    private sealed class Session
    {
        public Win32Input.EdgeZone Zone;
        public bool Pressed, Engaged, Captured;
        public System.Windows.Point StartDip;      // 按下时窗口相对坐标（判定阈值用）
        public PhysicalPoint StartPhys;            // 按下时光标物理坐标（增量基准）
        public double L0, T0, W0, H0;              // 按下时窗口矩形（DIP）
        public double Aspect;                      // 等比锁定的 H/W；0 = 自由缩放
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Window, Session> _sessions = new();

    /// <summary>命中源在需要按键的控件上时不参与窗口拖动（按钮/输入框/滚动条等）。</summary>
    public static bool IsInteractiveControlSource(DependencyObject? src)
    {
        while (src is not null)
        {
            if (src is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.Primitives.ScrollBar
                or System.Windows.Controls.Primitives.Thumb
                or System.Windows.Controls.Slider)
                return true;
            src = System.Windows.Media.VisualTreeHelper.GetParent(src)
                  ?? System.Windows.LogicalTreeHelper.GetParent(src);
        }
        return false;
    }

    /// <summary>给窗口挂载整面拖动 + 边缘缩放。edgeBandDip 为边缘缩放带宽；
    /// isInteractive 为空时整面可拖（贴图），否则仅非交互区可拖（面板/设置）；
    /// lockAspect 时缩放锁定纵横比（贴图：窗口非分层，破坏纵横比会在信箱区露黑边）。</summary>
    public static void Attach(Window window, double edgeBandDip,
        System.Predicate<DependencyObject?>? isInteractive = null, bool lockAspect = false)
    {
        var s = new Session();
        _sessions.Add(window, s);
        window.PreviewMouseLeftButtonDown += (_, e) => OnDown(window, s, e, edgeBandDip, isInteractive, lockAspect);
        window.PreviewMouseMove += (_, e) => OnMove(window, s, e, edgeBandDip);
        window.PreviewMouseLeftButtonUp += (_, e) => OnUp(window, s, e);
    }

    private static void OnDown(Window w, Session s, MouseButtonEventArgs e, double band,
        System.Predicate<DependencyObject?>? interactive, bool lockAspect)
    {
        Reset(w, s);
        if (e.ClickCount != 1) return;
        if (interactive?.Invoke(e.OriginalSource as DependencyObject) == true) return;
        var p = e.GetPosition(w);
        s.Zone = Win32Input.EdgeHitTest(new System.Windows.Size(w.ActualWidth, w.ActualHeight), p, band);
        s.StartDip = p;
        s.StartPhys = Win32Input.GetCursorPos();
        s.L0 = w.Left; s.T0 = w.Top; s.W0 = w.ActualWidth; s.H0 = w.ActualHeight;
        s.Aspect = lockAspect && s.W0 > 0 ? s.H0 / s.W0 : 0;
        s.Pressed = true;
        if (s.Zone != Win32Input.EdgeZone.None)
        {
            // 边缘即缩放意图：按下立即捕获鼠标。否则第一下移动就把光标带出窗口，
            // 窗外移动事件不送达，阈值永远达不到（WPF 按下时并不隐式捕获）
            w.CaptureMouse();
            s.Captured = true;
        }
    }

    private static void OnMove(Window w, Session s, MouseEventArgs e, double band)
    {
        if (!s.Pressed)
        {
            if (e.LeftButton == MouseButtonState.Released)
                w.Cursor = CursorFor(Win32Input.EdgeHitTest(
                    new System.Windows.Size(w.ActualWidth, w.ActualHeight), e.GetPosition(w), band));
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) { Reset(w, s); return; }

        if (!s.Engaged)
        {
            // 阈值区分点击与拖动，避免吞掉普通点击/双击
            var p = e.GetPosition(w);
            if (Math.Abs(p.X - s.StartDip.X) + Math.Abs(p.Y - s.StartDip.Y) < 4) return;
            s.Engaged = true;
            if (!s.Captured)
            {
                w.CaptureMouse();
                s.Captured = true;
            }
        }

        var cur = Win32Input.GetCursorPos();
        var scale = ScaleOf(w);
        var ddx = (cur.X - s.StartPhys.X) / scale;
        var ddy = (cur.Y - s.StartPhys.Y) / scale;
        ApplyDrag(s.Zone, s.L0, s.T0, s.W0, s.H0, ddx, ddy,
            MinOf(w.MinWidth, 40), MinOf(w.MinHeight, 30),
            double.IsPositiveInfinity(w.MaxWidth) ? double.PositiveInfinity : w.MaxWidth,
            double.IsPositiveInfinity(w.MaxHeight) ? double.PositiveInfinity : w.MaxHeight,
            s.Aspect,
            out var left, out var top, out var width, out var height);
        w.SetCurrentValue(Window.LeftProperty, left);
        w.SetCurrentValue(Window.TopProperty, top);
        w.SetCurrentValue(Window.WidthProperty, width);
        w.SetCurrentValue(Window.HeightProperty, height);
    }

    private static void OnUp(Window w, Session s, MouseButtonEventArgs e)
    {
        if (!s.Pressed) return;
        if (s.Engaged) e.Handled = true; // 拖动结束，别让这次抬起再触发点击语义
        if (s.Captured && w.IsMouseCaptured) w.ReleaseMouseCapture();
        Reset(w, s);
    }

    private static void Reset(Window w, Session s)
    {
        s.Pressed = false;
        s.Engaged = false;
        s.Captured = false;
        w.Cursor = null; // 恢复继承的默认光标
    }

    private static double MinOf(double v, double floor) => v is > 0 and < double.PositiveInfinity ? v : floor;

    private static double ScaleOf(Window w)
    {
        var m = System.Windows.PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        return m > 0 ? m : 1.0;
    }

    private static Cursor? CursorFor(Win32Input.EdgeZone zone) => zone switch
    {
        Win32Input.EdgeZone.Left or Win32Input.EdgeZone.Right => Cursors.SizeWE,
        Win32Input.EdgeZone.Top or Win32Input.EdgeZone.Bottom => Cursors.SizeNS,
        Win32Input.EdgeZone.TopLeft or Win32Input.EdgeZone.BottomRight => Cursors.SizeNWSE,
        Win32Input.EdgeZone.TopRight or Win32Input.EdgeZone.BottomLeft => Cursors.SizeNESW,
        _ => null,
    };

    /// <summary>纯函数：按命中方位与光标总位移（DIP）从按下时矩形推导新矩形。
    /// 每次都从起点推导（非增量累加），天然无漂移；Min/Max 钳制后左/上边缘侧同步补偿 Left/Top。</summary>
    public static void ApplyDrag(Win32Input.EdgeZone zone,
        double l0, double t0, double w0, double h0, double dx, double dy,
        double minW, double minH, double maxW, double maxH,
        out double left, out double top, out double width, out double height)
        => ApplyDrag(zone, l0, t0, w0, h0, dx, dy, minW, minH, maxW, maxH, 0,
            out left, out top, out width, out height);

    /// <summary>带纵横比锁定的重载：aspect = H/W（&gt;0 时生效）。
    /// 横向边缘/四角以宽度为主导推导高度；纵向边缘以高度为主导推导宽度；钳制后另一轴跟随保持比例。</summary>
    public static void ApplyDrag(Win32Input.EdgeZone zone,
        double l0, double t0, double w0, double h0, double dx, double dy,
        double minW, double minH, double maxW, double maxH, double aspect,
        out double left, out double top, out double width, out double height)
    {
        left = l0; top = t0; width = w0; height = h0;
        if (zone == Win32Input.EdgeZone.None)
        {
            left = l0 + dx;
            top = t0 + dy;
            return;
        }
        var touchL = zone is Win32Input.EdgeZone.Left or Win32Input.EdgeZone.TopLeft or Win32Input.EdgeZone.BottomLeft;
        var touchR = zone is Win32Input.EdgeZone.Right or Win32Input.EdgeZone.TopRight or Win32Input.EdgeZone.BottomRight;
        var touchT = zone is Win32Input.EdgeZone.Top or Win32Input.EdgeZone.TopLeft or Win32Input.EdgeZone.TopRight;
        var touchB = zone is Win32Input.EdgeZone.Bottom or Win32Input.EdgeZone.BottomLeft or Win32Input.EdgeZone.BottomRight;
        var horizontal = touchL || touchR;
        var vertical = touchT || touchB;

        if (aspect > 0)
        {
            if (horizontal)
            {
                // 四角与左右缘：宽度主导，高度随比例（钳制后回写宽度，两轴都在界内）
                width = Math.Clamp(w0 + (touchL ? -dx : dx), minW, maxW);
                height = Math.Clamp(width * aspect, minH, maxH);
                width = height / aspect;
                if (touchL) left = l0 + (w0 - width);
                if (touchT) top = t0 + (h0 - height);
            }
            else
            {
                // 上下缘：高度主导
                height = Math.Clamp(h0 + (touchT ? -dy : dy), minH, maxH);
                width = Math.Clamp(height / aspect, minW, maxW);
                height = width * aspect;
                if (touchT) top = t0 + (h0 - height);
            }
            return;
        }

        if (horizontal)
        {
            width = Math.Clamp(w0 + (touchL ? -dx : dx), minW, maxW);
            if (touchL) left = l0 + (w0 - width);
        }
        if (vertical)
        {
            height = Math.Clamp(h0 + (touchT ? -dy : dy), minH, maxH);
            if (touchT) top = t0 + (h0 - height);
        }
    }
}
