namespace Pasteup.App.Services;

/// <summary>窗口 DIP 尺寸记忆（2026-11-04）：用户拉过的宽高存 settings.json，重启后还原。
/// 存储值 0 = 未记忆（回退窗口默认）；显示器相关上限不在此钳——还原时 WindowLimits
/// 按所在屏工作区动态压回（跨屏/换 DPI 后存储值自动被 WPF 的 Max 强制改回合法域）。</summary>
public static class WindowSizeMemory
{
    private const double SanityMax = 20_000;

    /// <summary>存储值 → 还原值：不在 [min, 20000]（含 0/负/NaN/损坏值）一律回退 fallback。</summary>
    public static double Restore(double stored, double fallback, double min)
        => stored >= min && stored <= SanityMax ? stored : fallback;

    /// <summary>尺寸记录：值已稳定且与已存差 ≥1 DIP 才返回新值（Round 到整 DIP），
    /// 否则 null（调用方不写设置不触发落盘——还原触发的 SizeChanged 会经此静默）。</summary>
    public static (double W, double H)? Record(double width, double height,
        double storedWidth, double storedHeight)
    {
        if (width < 1 || height < 1 || double.IsNaN(width) || double.IsNaN(height)) return null;
        var w = Math.Round(width);
        var h = Math.Round(height);
        return Math.Abs(storedWidth - w) < 1 && Math.Abs(storedHeight - h) < 1
            ? null
            : (w, h);
    }
}
