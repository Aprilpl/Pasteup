using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Pasteup.Core.History;

namespace Pasteup.App.ViewModels;

public enum PreviewMode { Text, Image }

/// <summary>空格预览（Quick Look）视图模型：由面板选中条目驱动，展示逻辑全部为可单测纯函数。</summary>
public sealed partial class PreviewViewModel : ObservableObject
{
    [ObservableProperty]
    private PreviewMode _mode;

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _appInitial = "";

    /// <summary>来源应用图标（与面板行同缓存）；null = 未加载/提不到，头像回退首字母。</summary>
    [ObservableProperty]
    private BitmapSource? _appIcon;

    [ObservableProperty]
    private string _metaText = "";

    [ObservableProperty]
    private string _textContent = "";

    [ObservableProperty]
    private BitmapSource? _imageSource;

    /// <summary>图像加载完成态：true = 解码完成但无图（缓存被清理占位）；加载中为 false（不显示占位）。</summary>
    [ObservableProperty]
    private bool _isImageMissing;

    public ClipboardEntry? CurrentEntry { get; private set; }

    private CancellationTokenSource? _imageCts; // 当前图像条目的解码取消源（换条目/关窗即取消）
    private readonly SemaphoreSlim _imageGate = new(1, 1); // 单飞：同一时刻最多一张全尺寸解码在跑

    /// <param name="imageLoader">图像条目的全尺寸解码器（后台执行，返回已 Freeze 的 BitmapSource；
    /// 接收 CancellationToken，token 取消后应尽快返回 null；测试可传 null）。</param>
    /// <param name="iconLoader">来源应用图标加载器（缓存命中同步完成）；测试可传 null 维持首字母。</param>
    public void Update(ClipboardEntry entry,
        Func<ClipboardEntry, CancellationToken, Task<BitmapSource?>>? imageLoader = null,
        Func<ClipboardEntry, Task<BitmapSource?>>? iconLoader = null)
    {
        // 换条目先取消上一张的解码：连翻 ↑↓ 时旧解码完成也不再回写；窗口侧在 token 取消后
        // 会在读盘/解码前短路，排队中的旧请求不再白解一张大图（解码本身不可中断）。
        CancelPendingImageLoad();
        CurrentEntry = entry;
        Mode = ModeOf(entry.Kind);
        Title = BuildTitle(entry.SourceApp, entry.Kind);
        AppInitial = AppInitialOf(entry.SourceApp, entry.Kind);
        MetaText = BuildMetaText(entry);
        TextContent = BuildTextContent(entry);
        ImageSource = null;
        IsImageMissing = false;
        AppIcon = null; // 换条目先回字母，慢加载到了再盖（缓存命中 await 不让出、无闪烁）
        if (Mode == PreviewMode.Image && imageLoader is not null)
        {
            var cts = new CancellationTokenSource();
            _imageCts = cts;
            LoadImageAsync(entry, imageLoader, cts);
        }
        if (iconLoader is not null) LoadIconAsync(entry, iconLoader);
    }

    /// <summary>取消仍在进行的全尺寸图解码（换条目/窗口关闭时调用）；完成后由 token 兜底防回写。
    /// Cancel 后立刻 Dispose：此后只用 token 的 IsCancellationRequested，不涉及回调注册，安全。</summary>
    public void CancelPendingImageLoad()
    {
        var cts = Interlocked.Exchange(ref _imageCts, null);
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>后台解码全尺寸图（大图 ↑↓ 翻页不冻结 UI）；await 回 UI 上下文，换条目/取消后不回写旧结果。
    /// 单飞 + 取消：快速连翻时旧请求在排队或读盘前即被跳过，同一时刻最多一张全尺寸解码在跑
    ///（否则连按 ↑↓ 会让 2–4 张 ≤200MB 的解码并发，峰值内存与 CPU 随翻页次数叠加）。</summary>
    private async void LoadImageAsync(ClipboardEntry entry,
        Func<ClipboardEntry, CancellationToken, Task<BitmapSource?>> loader, CancellationTokenSource cts)
    {
        var token = cts.Token;
        var acquired = false;
        try
        {
            await _imageGate.WaitAsync(token);
            acquired = true;
            if (token.IsCancellationRequested) return; // 排队期间已翻走：不发起解码
            var img = await loader(entry, token);
            if (token.IsCancellationRequested || !ReferenceEquals(CurrentEntry, entry)) return;
            ImageSource = img;
            IsImageMissing = img is null;
        }
        catch (OperationCanceledException)
        {
            // 取消 = 用户已翻走：静默，不写占位（新条目有自己的加载态）
        }
        catch
        {
            if (!token.IsCancellationRequested && ReferenceEquals(CurrentEntry, entry)) IsImageMissing = true; // 解码失败同占位
        }
        finally
        {
            if (acquired) _imageGate.Release();
            // 正常收尾（未被取消）→ 摘掉并释放自己的取消源，避免 CTS 与已加载完成的条目一起驻留。
            // 用 CompareExchange 确认字段仍是自己再 Dispose，防并发下一轮请求替换后被误释放；
            // 被取消的 CTS 已由 Update/CancelPendingImageLoad 取消并 Dispose，这里不再触碰。
            if (!cts.IsCancellationRequested
                && ReferenceEquals(Interlocked.CompareExchange(ref _imageCts, null, cts), cts))
                cts.Dispose();
        }
    }

    /// <summary>后台补图标；await 回 UI 上下文，且换条目后不回写旧结果。</summary>
    private async void LoadIconAsync(ClipboardEntry entry, Func<ClipboardEntry, Task<BitmapSource?>> loader)
    {
        try
        {
            var icon = await loader(entry);
            if (ReferenceEquals(CurrentEntry, entry)) AppIcon = icon;
        }
        catch { /* 图标失败不影响预览 */ }
    }

    public static PreviewMode ModeOf(EntryKind kind) => kind == EntryKind.Image ? PreviewMode.Image : PreviewMode.Text;

    /// <summary>预览文本显示上限（32K 字符）：以 TextBlock 单次排版成本为目标——TextBlock 无
    /// 虚拟化（ScrollViewer 无限高测量下整段全量排版），MB 级全文一次排版可达数百 ms–秒级 UI
    /// 冻结且每次 ↑↓ 翻页重复付（2026-11-06 性能复审③；2026-11-10 复审从 256K 收紧到 32K）。
    /// 只截显示——取用粘贴、搜索、字数统计仍走完整 FullText。</summary>
    public const int MaxTextChars = 32 * 1024;

    public static string BuildTextContent(ClipboardEntry e)
    {
        var text = e.FullText ?? e.Preview;
        if (text.Length <= MaxTextChars) return text;
        return string.Concat(text.AsSpan(0, MaxTextChars).ToString(),
            $"\n\n…（预览仅显示前 {MaxTextChars:N0} 字符，全文共 {text.Length:N0} 字符；完整内容照常粘贴）");
    }

    public static string KindLabel(EntryKind kind) => kind switch
    {
        EntryKind.Image => "图像",
        EntryKind.Link => "链接",
        EntryKind.File => "文件",
        _ => "文本",
    };

    public static string BuildTitle(string? sourceApp, EntryKind kind)
        => $"{(string.IsNullOrWhiteSpace(sourceApp) ? "剪贴板" : sourceApp)} · {KindLabel(kind)}";

    /// <summary>页脚等宽元数据：图像 = 像素·格式·体积·时间；文本/链接/文件 = 字数·时间（原型 .pv-meta）。</summary>
    public static string BuildMetaText(ClipboardEntry e)
    {
        var time = Core.Text.TimeFormat.Format(e.UtcTimestamp.ToLocalTime());
        return e.Kind == EntryKind.Image
            ? $"{e.ImageWidth}×{e.ImageHeight} · PNG · {Core.Text.TextUtil.FormatSize(e.SizeBytes)} · {time}"
            : $"{(e.FullText ?? e.Preview).Length} 字 · {time}";
    }

    /// <summary>来源首字母头像（与面板行同一规则：File 用 ◧、空来源用 ·）。</summary>
    public static string AppInitialOf(string? sourceApp, EntryKind kind)
        => kind == EntryKind.File ? "◧"
         : string.IsNullOrEmpty(sourceApp) ? "·"
         : char.ToUpperInvariant(sourceApp[0]).ToString();
}
