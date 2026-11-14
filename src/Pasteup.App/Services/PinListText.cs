namespace Pasteup.App.Services;

/// <summary>托盘右键「贴图列表」子菜单的文案/结构（纯函数，无 WPF 依赖，可单测）。
/// 贴图 = <see cref="PinCoordinator"/> 持有的瞬时贴图窗口（Alt+S 框选截图生成，不入历史、
/// 关掉即散），所以子菜单在每次右键展开时按当前窗口实时重建：没有贴图时只有一行禁用提示，
/// 有贴图时每张一行（序号 + 原始物理尺寸），末尾分隔线接「全部关闭」。
/// 菜单项装配（缩略图/点击激活置前）见 App.RebuildPinListMenu。</summary>
public static class PinListText
{
    public const string Title = "贴图列表";
    public const string EmptyHint = "暂无贴图（Alt+S 框选截图可生成）";
    public const string CloseAll = "全部关闭";

    /// <summary>子菜单标题带当前贴图数（数量是瞬时状态，每次展开重算；0 张保持裸标题）。</summary>
    public static string Header(int count) => count <= 0 ? Title : $"{Title}（{count}）";

    /// <summary>单张贴图行：序号从 1 起（= 当前打开顺序；贴图窗口无标题/无持久化标识，
    /// 顺序 + 尺寸是用户唯一可辨认的口径），尺寸为原始物理像素。</summary>
    public static string ItemLabel(int index, int naturalWidth, int naturalHeight)
        => $"贴图 {index} · {naturalWidth}×{naturalHeight}";
}
