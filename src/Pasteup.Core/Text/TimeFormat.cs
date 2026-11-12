namespace Pasteup.Core.Text;

/// <summary>历史条目时间的展示格式（本地时区；展示刻意随系统文化，如 ddd 周几随 locale 中英）。</summary>
public static class TimeFormat
{
    public static string Format(DateTime local) => local switch
    {
        var t when t.Date == DateTime.Today => t.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture),
        var t when t.Date == DateTime.Today.AddDays(-1) => $"昨天 {t:HH:mm}",
        var t when t >= DateTime.Today.AddDays(-6) => $"{t:ddd} {t:HH:mm}",
        _ => local.ToString("M'月'd'日' HH:mm", System.Globalization.CultureInfo.CurrentCulture),
    };
}
