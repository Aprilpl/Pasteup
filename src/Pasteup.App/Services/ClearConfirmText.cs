namespace Pasteup.App.Services;

/// <summary>清空历史确认对话框的文案构建（纯函数，无状态，可单测）。
/// 数字段标记 <see cref="Segment.IsMono"/>，由调用方以等宽字体渲染。</summary>
public static class ClearConfirmText
{
    /// <summary>一段说明文字；数字段走等宽 tabular 字体与正文前景色。</summary>
    public readonly record struct Segment(string Text, bool IsMono);

    /// <summary>说明文案随「清空置顶/收藏的项目」勾选态实时改写：
    /// 不勾 = 保留置顶与收藏（引擎 Clear 默认连收藏一起保留，收藏 = 长期珍藏同置顶待遇）；
    /// 勾选 = 全清；两者皆无时无选项、只有全清一句。
    /// <param name="keep">置顶 ∪ 收藏 条目数（默认清空时保留的数量）。</param>
    /// <param name="favorite">收藏条目数（&gt;0 时文案提及「收藏」，否则只说「置顶」）。</param></summary>
    public static IReadOnlyList<Segment> Segments(int total, int keep, int favorite, bool clearPinned)
    {
        var scope = favorite > 0 ? "置顶/收藏" : "置顶";
        if (keep <= 0)
            return new[]
            {
                new Segment("将删除全部 ", false),
                new Segment(total.ToString(), true),
                new Segment(" 个项目；此操作不可撤销。", false),
            };

        return clearPinned
            ? new[]
            {
                new Segment("将删除全部 ", false),
                new Segment(total.ToString(), true),
                new Segment($" 个项目（含{scope} ", false),
                new Segment(keep.ToString(), true),
                new Segment(" 个）；此操作不可撤销。", false),
            }
            : new[]
            {
                new Segment("将删除 ", false),
                new Segment((total - keep).ToString(), true),
                new Segment(" 个项目，保留 ", false),
                new Segment(keep.ToString(), true),
                new Segment($" 个{scope}项目；此操作不可撤销。", false),
            };
    }

    /// <summary>没有置顶/收藏条目时复选框没有意义，整行隐藏。</summary>
    public static bool ShouldShowCheckbox(int keep) => keep > 0;
}
