namespace Pasteup.Core.History;

public enum EntryAgeGroup { Pinned, Today, Yesterday, ThisWeek, Earlier }

/// <summary>按本地时间把条目分到 今天/昨天/本周/更早（置顶条目独立成组且在最前）。</summary>
public static class EntryGrouping
{
    public static List<(EntryAgeGroup Group, ClipboardEntry Entry)> Classify(
        IEnumerable<ClipboardEntry> entries, DateTime nowLocal)
    {
        var today = nowLocal.Date;
        var yesterday = today.AddDays(-1);
        var weekAgo = today.AddDays(-6); // 本周含今天共 7 天窗口
        var result = new List<(EntryAgeGroup, ClipboardEntry)>();
        foreach (var e in entries)
        {
            var local = e.UtcTimestamp.ToLocalTime();
            EntryAgeGroup g = e.Pinned ? EntryAgeGroup.Pinned
                : local >= today ? EntryAgeGroup.Today
                : local >= yesterday ? EntryAgeGroup.Yesterday
                : local >= weekAgo ? EntryAgeGroup.ThisWeek
                : EntryAgeGroup.Earlier;
            result.Add((g, e));
        }
        return result;
    }

    public static string GroupLabel(EntryAgeGroup g) => g switch
    {
        EntryAgeGroup.Pinned => "置顶",
        EntryAgeGroup.Today => "今天",
        EntryAgeGroup.Yesterday => "昨天",
        EntryAgeGroup.ThisWeek => "本周",
        _ => "更早",
    };
}
