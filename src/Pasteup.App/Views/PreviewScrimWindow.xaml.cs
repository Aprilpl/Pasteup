using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Pasteup.App.Views;

/// <summary>预览遮罩窗口：剧院式全压（面板所在屏整屏压暗，预览唯一豁免浮层）。
/// 与预览同挂不抢焦点——键盘焦点始终留在面板，遮罩只拦鼠标。
/// 透明实现：AllowsTransparency + 背景=ScrimBrush（令牌自带 55% alpha），
/// 淡入淡出 = Window.Opacity 动画（透明窗上 WPF 原生渲染）。</summary>
public partial class PreviewScrimWindow : Window
{
    private const int FadeMs = 150;

    private bool _closing;

    /// <summary>用户点了暗处（= Space/Esc 同效收起；窗口自己只发信号）。</summary>
    public event Action? DismissRequested;

    public PreviewScrimWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Services.Win32Input.SetNoActivate(this);
        // 双保险：被点击时不激活（WS_EX_NOACTIVATE 位可能被样式重建抹掉，消息层兜底）
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_NOACTIVATE = 3; // 不激活、不吞点击
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return (IntPtr)MA_NOACTIVATE;
        }
        return IntPtr.Zero;
    }

    /// <summary>盖住目标屏（物理矩形 → 本窗 DIP）。</summary>
    public void CoverMonitor(Pasteup.Core.Capture.MonitorRect m)
    {
        var (left, top, width, height) = Services.PreviewLayout.CoverRect(m, m.Dpi);
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public void ShowScrim()
    {
        _closing = false;
        Opacity = 0;
        Show();
        FadeTo(1);
    }

    public void HideScrim()
    {
        if (!IsVisible) return;
        _closing = true;
        FadeTo(0);
        // 动画 Completed 在「淡入未完即接淡出」交接下实测不触发——定时器确定性收尾，
        // 绝不把用户锁在遮罩后面
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FadeMs + 60) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_closing && IsVisible)
            {
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;
                Hide();
            }
        };
        t.Start();
    }

    private void FadeTo(double value)
    {
        var anim = new DoubleAnimation(value, TimeSpan.FromMilliseconds(FadeMs))
        {
            EasingFunction = new QuadraticEase(),
        };
        BeginAnimation(OpacityProperty, anim);
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e)
    {
        // 淡出的 150ms 内忽略点击，防止误重开
        if (!_closing) DismissRequested?.Invoke();
    }
}
