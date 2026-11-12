namespace Pasteup.Core.Text;

/// <summary>预览文本与链接识别的纯函数。</summary>
public static class TextUtil
{
    /// <summary>整段文本是单个 URL 时判定为链接（Link 类型条目）。</summary>
    public static bool IsSingleUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.Contains(' ') || t.Contains('\n') || t.Contains('\r') || t.Contains('\t')) return false;
        return t.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            || t.StartsWith("www.", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>折叠空白为单空格并截断，用于列表预览。
    /// 流式折叠 + 早停：攒够 max 个折叠字符即收手——原实现全量 Split 把 10MB 文本拆成
    /// ~170 万个小字符串再 Join，只为截 200 字符预览（2026-11-04 审计 C2）。
    /// 语义与「全量折叠后截断」一致：短文本逐字相同，长文本前 max-1 字符 + "…" 相同。</summary>
    public static string MakePreview(string? text, int max = 200)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new System.Text.StringBuilder(max + 8);
        var remaining = text.AsSpan();
        while (sb.Length < max && !remaining.IsEmpty)
        {
            var start = 0; // 跳过前导空白（char.IsWhiteSpace = Split(null) 同款 Unicode 空白口径）
            while (start < remaining.Length && char.IsWhiteSpace(remaining[start])) start++;
            if (start >= remaining.Length) break;
            remaining = remaining[start..];
            var wordEnd = 0; // 取一段连续非空白
            while (wordEnd < remaining.Length && !char.IsWhiteSpace(remaining[wordEnd])) wordEnd++;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(remaining[..wordEnd]);
            remaining = remaining[wordEnd..];
        }
        var collapsed = sb.ToString();
        return collapsed.Length <= max ? collapsed : collapsed[..(max - 1)] + "…";
    }

    /// <summary>Windows 文件大小显示。</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        < 0 => "0 B",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB",
    };
}
