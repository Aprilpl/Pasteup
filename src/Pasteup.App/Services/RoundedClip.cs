using System.Windows;
using System.Windows.Media;

namespace Pasteup.App.Services;

/// <summary>
/// 把元素内容裁剪进圆角矩形。WPF 的 Border.CornerRadius 只圆化自身背景/边框的绘制、不裁剪子元素，
/// 贴角的子元素（如标题栏按钮的悬停底色）会画出圆角框外，形成直角外溢。
/// 挂在圆角外框的内层 Grid 上，Radius 与外框 CornerRadius 同令牌，随尺寸变化自动更新 Clip。
/// </summary>
public static class RoundedClip
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(RoundedClip), new PropertyMetadata(false, OnEnabledChanged));

    public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
        "Radius", typeof(CornerRadius), typeof(RoundedClip), new PropertyMetadata(new CornerRadius(0), OnRadiusChanged));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);
    public static CornerRadius GetRadius(DependencyObject obj) => (CornerRadius)obj.GetValue(RadiusProperty);
    public static void SetRadius(DependencyObject obj, CornerRadius value) => obj.SetValue(RadiusProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            fe.SizeChanged += OnSizeChanged;
            Apply(fe);
        }
        else
        {
            fe.SizeChanged -= OnSizeChanged;
            fe.Clip = null;
        }
    }

    private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement { Clip: not null } fe) Apply(fe);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e) => Apply((FrameworkElement)sender);

    private static void Apply(FrameworkElement fe)
    {
        // 令牌均为四角一致半径，取 TopLeft 即可。尺寸/半径未变（如主题字典整替换后令牌
        // 重解析出同值触发 RadiusChanged）时复用现有几何，免重建+免重挂裁剪
        var r = GetRadius(fe).TopLeft;
        if (fe.Clip is RectangleGeometry { Rect: { X: 0, Y: 0, Width: var gw, Height: var gh }, RadiusX: var gr }
            && gw == fe.RenderSize.Width && gh == fe.RenderSize.Height && gr == r)
            return;
        var geo = new RectangleGeometry(new Rect(new Point(0, 0), fe.RenderSize), r, r);
        if (geo.CanFreeze) geo.Freeze();
        fe.Clip = geo;
    }
}
