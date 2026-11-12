using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pasteup.Core.History;

namespace Pasteup.App.ViewModels;

/// <summary>顺序对齐原型分类行：全部 / 收藏 / 置顶 / 今天 / 文本 / 图像 / 链接 / 文件。</summary>
public enum PanelTab { All, Favorite, Pinned, Today, Text, Image, Link, File }

/// <summary>胶囊圆角 = 控件实高一半；WPF 对 CornerRadius 不做 CSS 式钳制，999 会渲染成椭圆。</summary>
public sealed class PillConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new CornerRadius(Math.Max(0, value is double h ? h : 0) / 2);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed partial class TabVm : ObservableObject
{
    private readonly Func<int> _count;

    /// <param name="glyph">FluentSystemIcons 码点（映射见 docs/rules/图标规范.md）。</param>
    /// <param name="filled">true = 用 Filled 字体（FluentSystemIconsFilled），如置顶图钉。</param>
    public TabVm(PanelTab tab, string label, string glyph, bool filled, Func<int> count)
    {
        Tab = tab;
        Label = label;
        Glyph = glyph;
        Filled = filled;
        _count = count;
        _countValue = count();
    }

    public PanelTab Tab { get; }
    public string Label { get; }
    public string Glyph { get; }
    public bool Filled { get; }

    [ObservableProperty]
    private int _countValue;

    [ObservableProperty]
    private bool _isActive;

    public void RefreshCount() => CountValue = _count();
}

public partial class GroupVm : ObservableObject
{
    public bool IsGroup => true;
    public string Label { get; }

    /// <summary>可观测可写：拼接差量更新保留组头实例时原地刷新计数
    ///（对齐键只看标签——成员增删不断前缀，否则任何增删都退化为全量替换）。</summary>
    [ObservableProperty]
    private string _countText;

    public GroupVm(string label, int count)
    {
        Label = label;
        _countText = count.ToString();
    }
}

public partial class RowVm : ObservableObject
{
    public bool IsGroup => false;
    public required ClipboardEntry Entry { get; init; }
    // 易变字段可观测 + 可写：拼接差量更新会复用行实例（容器不换），Touch/收藏/置顶后原地刷新
    [ObservableProperty]
    private string _timeText = "";
    // TimeText 对应的 UtcTimestamp.Ticks：拼接刷新用一次整数比较判定是否需要重格式化，
    // 代替对全部保留行无条件重算（千条=千次字符串格式化的白做功）
    internal long TimeTicks;
    public string AppInitial { get; init; } = "";

    /// <summary>来源应用图标（48px 提取后冻结，跨线程安全）；null = 未加载/提不到，行上回退首字母。</summary>
    [ObservableProperty]
    private System.Windows.Media.ImageSource? _appIcon;

    // 旗标下沉基类（原 Text/MediaRow 各自重复声明；图像瓦片继承后同样具备，
    // 消费侧 ActiveEntry/选中恢复/刷新对齐键 全部单臂收敛——2026-11-05 D1 还债）
    [ObservableProperty]
    private bool _pinned;
    [ObservableProperty]
    private bool _favorite;
}

public sealed partial class TextRowVm : RowVm
{
    public string Preview { get; init; } = "";
}

public sealed partial class MediaRowVm : RowVm
{
    public string Title { get; init; } = "";
    /// <summary>副行元信息 = 来源应用名（与 Title 的宽高·体积互补；创建后不变——
    /// 条目被同内容替换时是新 VM 进替换段，无原地刷新需求。曾与 TimeText 同串双显）。</summary>
    public string Meta { get; init; } = "";
    public string? ImageFile { get; init; }
}

/// <summary>图像 Tab 单列瓦片：继承 RowVm 后与行同构（旗标/来源图标/时间皆备）。</summary>
public sealed partial class MediaTileVm : RowVm
{
    public string? ImageFile { get; init; }
}

/// <summary>唤起面板主视图模型：标签过滤 + 搜索 + 分组列表 / 图像网格。</summary>
public sealed partial class PanelViewModel : ObservableObject
{
    private readonly HistoryEngine _engine;
    private readonly Services.SourceAppIconService? _icons;
    private long _lastRefreshVersion = -1;
    private long _lastCountVersion = -1;
    // 8 个 Tab 的计数缓存：一次加锁单遍算出（HistoryEngine.Counts），TabVm 的 Func 从这里读，
    // 版本变化时整体换新再 RefreshCount 到各 Tab（原先每个 Tab 各自查询 = 7 趟全表扫描）
    private HistoryCounts _counts;

    public ObservableCollection<TabVm> Tabs { get; }
    public ObservableCollection<object> Items => _rows;
    private readonly ResettableList _rows = new();

    [ObservableProperty]
    private PanelTab _selectedTab = PanelTab.All;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private object? _selectedItem;

    [ObservableProperty]
    private string _countText = "0 条";

    public bool IsGridMode => SelectedTab == PanelTab.Image;

    /// <param name="icons">来源应用图标服务；null = 跳过加载（测试工厂沿用旧签名）。</param>
    public PanelViewModel(HistoryEngine engine, Services.SourceAppIconService? icons = null)
    {
        _engine = engine;
        _icons = icons;
        // 构造期先把 8 个 Tab 的计数一次算出（一次加锁单遍）：TabVm 构造即调用各自的 Func，
        // 必须已有缓存值；_lastCountVersion 对齐引擎版本，首次 RefreshFromEngine 不必再白扫一遍
        _counts = _engine.Counts(DateTime.Now.Date);
        _lastCountVersion = _engine.Version;
        Tabs =
        [
            new(PanelTab.All, "全部", "\uE6C5", false, () => _counts.Total),           // grid-dots-24-regular
            new(PanelTab.Favorite, "收藏", "\uF710", false, () => _counts.Favorite),  // star-24-regular
            new(PanelTab.Pinned, "置顶", "\uF60C", true, () => _counts.Pinned),        // pin-24-filled
            new(PanelTab.Today, "今天", "\uF2DE", false, () => _counts.Today),         // clock-24-regular
            new(PanelTab.Text, "文本", "\uF379", false, () => _counts.Text),           // document-24-regular
            new(PanelTab.Image, "图像", "\uF489", false, () => _counts.Image),         // image-24-regular
            new(PanelTab.Link, "链接", "\uF4E5", false, () => _counts.Link),           // link-24-regular
            new(PanelTab.File, "文件", "\uF419", false, () => _counts.File),           // folder-24-regular
        ];
        // 面板隐藏期间剪贴板事件不整表重建（托盘驻留全天候，每次复制都重建千行级列表纯属白做功）：
        // 版本门控天然兜底——隐藏期 Version 已走远，下次唤起 RefreshFromEngine 必不等而补刷
        //（2026-11-06 性能复审⑦；SettingsViewModel 的防抖自动保存经 App 独立订阅，不受影响）。
        // 默认 true：VM 视「未被窗口告知隐藏」为可见——既有调用方（含测试）的自动刷新语义不变。
        engine.Changed += () => { if (IsPanelVisible) RefreshFromEngine(); };
    }

    /// <summary>面板当前是否可见（窗口 ShowAtCursor 置 true / HidePanel 置 false；默认 true）。
    /// 隐藏期引擎事件被跳过，靠版本门控在下次唤起补刷。</summary>
    public bool IsPanelVisible { get; set; } = true;

    [RelayCommand]
    private void SelectTab(object? param)
    {
        if (param is PanelTab tab && tab != SelectedTab) SelectedTab = tab;
    }

    partial void OnSelectedTabChanged(PanelTab value) => RefreshFromEngine(force: true);

    partial void OnSearchTextChanged(string value) => RefreshFromEngine(force: true);

    /// <summary>当前选中项对应的条目（行/图像瓦片同构于 RowVm，单臂判定）。</summary>
    public ClipboardEntry? ActiveEntry => SelectedItem switch
    {
        RowVm r => r.Entry,
        _ => null,
    };

    /// <summary>
    /// ↑↓ 移动选中：仅在条目行间移动（跳过组头），首尾停住不循环；
    /// 无选中时 ↓ 落第一条、↑ 落最后一条。
    /// </summary>
    public void MoveSelection(int delta)
    {
        var rows = Items.Where(i => i is not GroupVm).ToList();
        if (rows.Count == 0) return;
        var idx = SelectedItem is null ? -1 : rows.IndexOf(SelectedItem);
        idx = idx < 0
            ? (delta > 0 ? 0 : rows.Count - 1)
            : Math.Clamp(idx + delta, 0, rows.Count - 1);
        SelectedItem = rows[idx];
    }

    /// <summary>
    /// 唤起回顶：选中首个非组头行（组头不可选惯例；空列表得 null 自然无害）。
    /// 须在 RefreshFromEngine 版本门控之外调用——引擎未变时 refresh 直接跳过、
    /// 选中恢复不跑，放在门控内做重置会时灵时不灵。
    /// </summary>
    public void ResetToTop() => SelectedItem = Items.FirstOrDefault(i => i is not GroupVm);

    /// <summary>
    /// 重建列表视图。force=false 时版本门控：引擎数据自上次刷新以来未变且列表非空则直接跳过——
    /// 唤起面板（ShowAtCursor）每次强制重建会在瞬态布局期（Items 清空重填、滚动偏移往返）吃掉
    /// 用户「唤起后立刻点击」的鼠标事件（点中错误行或落空，2026-11-03 实测 160-640ms 窗口 100% 错行）；
    /// 跳过重建则视觉树原样保留，点击永远落在稳定容器上，还顺带省掉千行级重建开销。
    /// Tab/搜索等改变视图口径的调用必须 force=true。
    /// </summary>
    public void RefreshFromEngine(bool force = false)
    {
        if (!force && Items.Count > 0 && _engine.Version == _lastRefreshVersion) return;
        _lastRefreshVersion = _engine.Version;
        EntryKind? kind = SelectedTab switch
        {
            PanelTab.Text => EntryKind.Text,
            PanelTab.Image => EntryKind.Image,
            PanelTab.Link => EntryKind.Link,
            PanelTab.File => EntryKind.File,
            _ => null,
        };
        Func<ClipboardEntry, bool>? filter = SelectedTab switch
        {
            PanelTab.Favorite => static e => e.Favorite,
            PanelTab.Pinned => static e => e.Pinned,
            PanelTab.Today => static e => e.UtcTimestamp.ToLocalTime() >= DateTime.Now.Date,
            _ => null,
        };
        var entries = _engine.Query(kind, SearchText, filter);
        // Tab 计数与搜索词/所选 Tab 无关（各 Tab 自带全库过滤谓词）：仅引擎版本变化时重算，
        // 否则每个搜索防抖周期都要白跑 7 趟全表扫描。现在一次加锁单遍出全部 8 个数，
        // Tab 的 Func 从 _counts 缓存读，RefreshCount 对外行为不变。
        if (_engine.Version != _lastCountVersion)
        {
            _lastCountVersion = _engine.Version;
            _counts = _engine.Counts(DateTime.Now.Date);
            foreach (var t in Tabs) t.RefreshCount();
        }
        foreach (var t in Tabs) t.IsActive = t.Tab == SelectedTab;

        var selectedId = ActiveEntry?.Id;
        // 先只算「键序列」（组头标签 / 行 Id+具体类型）求新旧公共前后缀，再只 materialize 变化
        // 中段——原实现先为全部条目建 VM+格式化时间、再在拼接里丢弃前后缀，千条列表每次刷新
        // 都白做一遍（2026-11-11 第 3 批 P1-2）。分类口径与分组渲染完全一致：一次 Classify。
        var keys = BuildRefreshKeys(entries, filter is not null, out var groupCounts);
        var oldCount = _rows.Count;
        var p = 0;
        while (p < oldCount && p < keys.Count && MatchesKey(_rows[p], keys[p])) p++;
        var s = 0;
        while (s < oldCount - p && s < keys.Count - p
               && MatchesKey(_rows[oldCount - 1 - s], keys[keys.Count - 1 - s])) s++;

        if (p == 0 && s == 0)
        {
            // 重建路径（首尾全不对齐 = 零保留实例，如搜索词变更/Tab 切换整表换血）：单次 Reset
            // 替代 2N 次逐项通知与 O(N²) 列表搬移——容器本就全换，无拼接语义可损失。
            var next = new List<object>(keys.Count);
            foreach (var key in keys) next.Add(MaterializeKey(key, groupCounts));
            _rows.ResetAll(next);
        }
        else
        {
            // 拼接路径：只 materialize 变化中段 [p, keys.Count - s)，前后缀段原 VM 实例整段复用
            //（容器不换=无瞬态布局，点击不落空）；逐项 Remove/Insert 的细粒度通知是容器保留的前提。
            var removeCount = oldCount - p - s;
            for (var i = 0; i < removeCount; i++) _rows.RemoveAt(p);
            var midEnd = keys.Count - s;
            for (var i = p; i < midEnd; i++) _rows.Insert(i, MaterializeKey(keys[i], groupCounts));
            // 保留段原地刷新：组头计数按 label 从本次分组统计取；行/瓦片时间（Ticks 门控）与旗标。
            // 中段新 VM 创建时已带新值，不重复刷。
            RefreshRetained(0, p, groupCounts);
            RefreshRetained(midEnd, _rows.Count, groupCounts);
        }

        CountText = $"{entries.Count} 条";
        // 恢复选中：按 Id 找回原条目；找不到（含无先前选中）落到首个非组头行。
        // 组头不可选——旧谓词的「selectedId is null」臂会让全新唤起把第一项组头选上，
        // ActiveEntry 为 null 且焦点无处落（2026-11-01 空格预览实机冒烟踩出）
        SelectedItem = Items.FirstOrDefault(i => i is RowVm r && r.Entry.Id == selectedId)
            ?? Items.FirstOrDefault(i => i is not GroupVm);
        OnPropertyChanged(nameof(IsGridMode));
        QueueIconLoads();
    }

    /// <summary>刷新对齐键（只含引用/标量）：组头=标签；行/瓦片=条目 Id + 行 VM 具体类型。
    /// 类型是承重墙：同 Id 的 MediaRow 与 MediaTile 不得互相顶替（DataTemplate 按运行时类型解析，
    /// 跨 Tab 误对齐会让图像页显示出「行」）。</summary>
    private readonly record struct RefreshKey(bool IsGroup, string Label, ClipboardEntry? Entry, Type? RowType);

    /// <summary>
    /// 派生「新键序列」并顺带做一次分组统计——不 materialize 任何 VM、不格式化时间。
    /// Tab/搜索口径与旧实现逐支一致：图像 Tab 每图一行瓦片；收藏/置顶/今天平铺无组头；
    /// 其余（全部与类型 Tab）按年龄分组、组头只在标签变化处出现。
    /// </summary>
    private List<RefreshKey> BuildRefreshKeys(IReadOnlyList<ClipboardEntry> entries, bool flat,
        out Dictionary<string, int>? groupCounts)
    {
        groupCounts = null;
        var keys = new List<RefreshKey>(entries.Count + 8);
        if (SelectedTab == PanelTab.Image)
        {
            // 单列全宽瓦片：每图一行，保持引擎顺序（置顶最上 + 其余复制时间倒序）；
            // 与行同构携带旗标/首字母（来源图标经 QueueIconLoads 的 OfType<RowVm> 自动覆盖）
            foreach (var e in entries) keys.Add(RowKey(e, typeof(MediaTileVm)));
            return keys;
        }
        if (flat)
        {
            // 收藏/置顶/今天标签：整表同组，不重复加组头（收藏 Tab 平铺，同置顶惯例）
            foreach (var e in entries) keys.Add(RowKey(e, RowTypeOf(e)));
            return keys;
        }
        var classified = EntryGrouping.Classify(entries, DateTime.Now);
        // 一次分组统计：既给中段新建的组头取 CountText，也给保留段组头原地刷新
        //（旧实现每个组头都靠 GroupBy/全表扫描取计数）
        groupCounts = new Dictionary<string, int>();
        foreach (var (group, _) in classified)
        {
            var label = EntryGrouping.GroupLabel(group);
            groupCounts[label] = groupCounts.TryGetValue(label, out var seen) ? seen + 1 : 1;
        }
        string? currentLabel = null;
        foreach (var (group, entry) in classified)
        {
            var label = EntryGrouping.GroupLabel(group);
            if (currentLabel != label)
            {
                keys.Add(new RefreshKey(true, label, null, null));
                currentLabel = label;
            }
            keys.Add(RowKey(entry, RowTypeOf(entry)));
        }
        return keys;
    }

    private static RefreshKey RowKey(ClipboardEntry entry, Type rowType) => new(false, "", entry, rowType);

    /// <summary>条目在非图像 Tab 下的行 VM 具体类型（与 CreateRow 的产出一致，类型进对齐键）。</summary>
    private static Type RowTypeOf(ClipboardEntry entry) =>
        entry.Kind == EntryKind.Image ? typeof(MediaRowVm) : typeof(TextRowVm);

    /// <summary>现有 VM 与刷新键是否同一槽位：组头看标签，行/瓦片看 Id+具体类型。</summary>
    private static bool MatchesKey(object vm, in RefreshKey key) => vm switch
    {
        GroupVm g => key.IsGroup && g.Label == key.Label,
        RowVm r => !key.IsGroup && key.Entry is { } e && r.Entry.Id == e.Id && r.GetType() == key.RowType,
        _ => false,
    };

    /// <summary>把刷新键 materialize 成 VM（只在需要落列表的位置调用：重建路径全量、拼接路径仅中段）。
    /// 组头的 CountText 取自本次分组统计；行/瓦片与旧实现逐字段同构。</summary>
    private object MaterializeKey(in RefreshKey key, Dictionary<string, int>? groupCounts)
    {
        if (key.IsGroup)
        {
            var count = groupCounts is not null && groupCounts.TryGetValue(key.Label, out var c) ? c : 0;
            return new GroupVm(key.Label, count);
        }
        var entry = key.Entry!; // 行键必带条目（见 RowKey 构造约束）
        return key.RowType == typeof(MediaTileVm) ? CreateTile(entry) : CreateRow(entry);
    }

    /// <summary>保留段原地刷新：组头计数按 label 从本次分组统计刷新；行/瓦片时间（Ticks 门控）与旗标。
    /// 中段新 VM 创建时已带新值，不在这里重复刷（对全部保留行无条件重算是千次级白做功）。</summary>
    private void RefreshRetained(int from, int to, Dictionary<string, int>? groupCounts)
    {
        for (var i = from; i < to; i++)
            switch (_rows[i])
            {
                case GroupVm g:
                    if (groupCounts is not null && groupCounts.TryGetValue(g.Label, out var count))
                    {
                        var text = count.ToString();
                        if (text != g.CountText) g.CountText = text;
                    }
                    break;
                case RowVm row:
                    RefreshRowTime(row, row.Entry);
                    row.Pinned = row.Entry.Pinned;
                    row.Favorite = row.Entry.Favorite;
                    break;
            }
    }

    /// <summary>图像 Tab 单列全宽瓦片（与旧内联构造逐字段一致）。</summary>
    private static MediaTileVm CreateTile(ClipboardEntry e) => new()
    {
        Entry = e,
        TimeText = Core.Text.TimeFormat.Format(e.UtcTimestamp.ToLocalTime()),
        TimeTicks = e.UtcTimestamp.Ticks,
        ImageFile = e.ImageFile,
        Pinned = e.Pinned,
        Favorite = e.Favorite,
        AppInitial = PreviewViewModel.AppInitialOf(e.SourceApp, e.Kind),
    };

    /// <summary>时间戳有变（Ticks 不等）才重格式化 TimeText 并回写 Ticks 缓存；同值不产生任何分配与通知。</summary>
    private static void RefreshRowTime(RowVm row, ClipboardEntry entry)
    {
        if (entry.UtcTimestamp.Ticks == row.TimeTicks) return;
        var local = entry.UtcTimestamp.ToLocalTime();
        row.TimeTicks = entry.UtcTimestamp.Ticks;
        row.TimeText = Core.Text.TimeFormat.Format(local);
    }

    /// <summary>行为后台补来源图标：按 KeyOf 分组去重（典型 ≤10 个唯一应用），
    /// 缓存命中 await 不让出、未命中走后台线程提取；await 后回 UI 上下文赋值。</summary>
    private void QueueIconLoads()
    {
        if (_icons is null) return;
        foreach (var g in Items.OfType<RowVm>()
                               .GroupBy(r => Services.SourceAppIconService.KeyOf(r.Entry))
                               .Where(g => g.Key.Length > 0))
            LoadIconGroup(g.ToList());
    }

    private async void LoadIconGroup(List<RowVm> rows)
    {
        try
        {
            var icon = await _icons!.GetAsync(rows[0].Entry);
            if (icon is null) return; // 提不到：行为保持首字母回退
            foreach (var r in rows) r.AppIcon = icon;
        }
        catch { /* 图标失败不影响列表 */ }
    }

    private static RowVm CreateRow(ClipboardEntry e)
    {
        var local = e.UtcTimestamp.ToLocalTime();
        // 首字母规则与空格预览头像同源（PreviewViewModel.AppInitialOf）
        var appInitial = PreviewViewModel.AppInitialOf(e.SourceApp, e.Kind);
        if (e.Kind == EntryKind.Image)
        {
            return new MediaRowVm
            {
                Entry = e,
                TimeText = Core.Text.TimeFormat.Format(local),
                TimeTicks = e.UtcTimestamp.Ticks,
                AppInitial = appInitial,
                Title = $"{e.ImageWidth}×{e.ImageHeight} · {Core.Text.TextUtil.FormatSize(e.SizeBytes)}",
                Meta = e.SourceApp ?? "", // 副行 = 来源应用名（与 Title 互补，不再与时间同串双显）
                ImageFile = e.ImageFile,
                Pinned = e.Pinned,
                Favorite = e.Favorite,
            };
        }
        return new TextRowVm
        {
            Entry = e,
            TimeText = Core.Text.TimeFormat.Format(local),
            TimeTicks = e.UtcTimestamp.Ticks,
            AppInitial = e.Kind == EntryKind.File ? "◧" : appInitial,
            Preview = e.Preview,
            Pinned = e.Pinned,
            Favorite = e.Favorite,
        };
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (ActiveEntry is { } e) _engine.Remove(e.Id);
    }

    [RelayCommand]
    private void TogglePin()
    {
        if (ActiveEntry is { } e) _engine.TogglePin(e.Id);
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (ActiveEntry is { } e) _engine.ToggleFavorite(e.Id);
    }
}

/// <summary>支持整表替换单次 Reset 通知的列表（仅重建路径用；拼接路径走基类逐项操作，
/// 细粒度通知是容器保留的前提）。Reset 参数无载荷，EventArgs 静态复用（WPF 自家集合同款做法）。</summary>
internal sealed class ResettableList : ObservableCollection<object>
{
    private static readonly NotifyCollectionChangedEventArgs ResetArgs = new(NotifyCollectionChangedAction.Reset);
    private static readonly PropertyChangedEventArgs CountArgs = new("Count");
    private static readonly PropertyChangedEventArgs IndexerArgs = new("Item[]");

    public void ResetAll(List<object> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var i in items) Items.Add(i);
        OnPropertyChanged(CountArgs);
        OnPropertyChanged(IndexerArgs);
        OnCollectionChanged(ResetArgs);
    }
}
