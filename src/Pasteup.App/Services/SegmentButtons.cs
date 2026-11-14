using System.Windows.Controls;

namespace Pasteup.App.Services;

/// <summary>
/// 设置页「分段选择钮」选中态（主题三钮 / 面板锚点两钮共用）。
/// 颜色一律走 <see cref="System.Windows.FrameworkElement.SetResourceReference"/>（即代码版 DynamicResource）：
/// 主题切换是整体替换 App.Resources 合并字典位置 0，局部值里存下的旧画刷实例不会跟着变；
/// 曾因此出现「浅色主题下两个锚点钮仍是深色药丸」的突兀观感（2026-11-14 用户报）。
/// 只写资源引用，不写 Brush 实例——这条不变量由 SegmentButtonTests 锁定。
/// </summary>
public static class SegmentButtons
{
    public static void SetActive(Button button, bool active)
    {
        button.SetResourceReference(Control.BackgroundProperty, active ? "AccentTintBrush" : "ControlBgBrush");
        button.SetResourceReference(Control.ForegroundProperty, active ? "AccentBrush" : "TextBrush");
    }
}
