using System.Windows.Media;
using System.Windows.Media.Imaging;
using Pasteup.App.Services;
using Pasteup.App.ViewModels;
using Pasteup.Core.Capture;
using Pasteup.Core.History;
using Xunit;
using EntryFactory = Pasteup.Tests.PreviewHelpers;

namespace Pasteup.Tests;

/// <summary>空格预览：VM 展示纯函数（类型标签/标题/元数据/首字母）+ 落点数学
/// + 图像解码取消/单飞 + 文本显示上限。</summary>
public class PreviewTests
{
    // ---- 模式与标签 ----

    [Theory]
    [InlineData(EntryKind.Image, PreviewMode.Image)]
    [InlineData(EntryKind.Text, PreviewMode.Text)]
    [InlineData(EntryKind.Link, PreviewMode.Text)]
    [InlineData(EntryKind.File, PreviewMode.Text)]
    public void Mode_of_kind(EntryKind kind, PreviewMode expected)
        => Assert.Equal(expected, PreviewViewModel.ModeOf(kind));

    [Theory]
    [InlineData(EntryKind.Image, "图像")]
    [InlineData(EntryKind.Text, "文本")]
    [InlineData(EntryKind.Link, "链接")]
    [InlineData(EntryKind.File, "文件")]
    public void Kind_labels(EntryKind kind, string expected)
        => Assert.Equal(expected, PreviewViewModel.KindLabel(kind));

    // ---- 标题：来源 · 类型 ----

    [Theory]
    [InlineData("WeChat", EntryKind.Text, "WeChat · 文本")]
    [InlineData(null, EntryKind.Image, "剪贴板 · 图像")]
    [InlineData("", EntryKind.Link, "剪贴板 · 链接")]
    public void Title_is_source_and_kind(string? sourceApp, EntryKind kind, string expected)
        => Assert.Equal(expected, PreviewViewModel.BuildTitle(sourceApp, kind));

    // ---- 首字母头像：与面板行同规则 ----

    [Theory]
    [InlineData("wechat", EntryKind.Text, "W")]
    [InlineData(null, EntryKind.Text, "·")]
    [InlineData("", EntryKind.Text, "·")]
    [InlineData("explorer", EntryKind.File, "◧")]
    [InlineData(null, EntryKind.File, "◧")]
    public void App_initial_rules(string? sourceApp, EntryKind kind, string expected)
        => Assert.Equal(expected, PreviewViewModel.AppInitialOf(sourceApp, kind));

    // ---- 页脚元数据 ----

    [Fact]
    public void Text_meta_is_char_count_and_time()
    {
        var e = EntryFactory.Text("hello world", "Notes");
        e = EntryFactory.WithTime(e, EntryFactory.Today(21, 33));
        Assert.Equal($"11 字 · 21:33", PreviewViewModel.BuildMetaText(e));
    }

    [Fact]
    public void Link_meta_is_char_count_and_time()
    {
        var url = "https://gemini.google.com/app";
        var e = EntryFactory.WithTime(EntryFactory.Text(url, "Chrome"), EntryFactory.Today(10, 5));
        Assert.Equal(EntryKind.Link, e.Kind); // 单 URL 会被归类为链接，元数据仍按字数
        Assert.Equal($"{url.Length} 字 · 10:05", PreviewViewModel.BuildMetaText(e));
    }

    [Fact]
    public void Image_meta_is_dims_format_size_and_time()
    {
        var e = EntryFactory.WithTime(EntryFactory.Image(1280, 836, 214 * 1024), EntryFactory.Today(21, 32));
        Assert.Equal("1280×836 · PNG · 214 KB · 21:32", PreviewViewModel.BuildMetaText(e));
    }

    [Fact]
    public void Meta_uses_full_date_for_old_entries()
    {
        var local = DateTime.Today.AddDays(-20).AddHours(8); // 保证落在 M月d日 分支（与运行日期无关）
        var e = EntryFactory.WithTime(EntryFactory.Text("abc", "Notes"), local.ToUniversalTime());
        Assert.Equal($"3 字 · {Core.Text.TimeFormat.Format(local)}", PreviewViewModel.BuildMetaText(e));
    }

    // ---- VM.Update ----

    [Fact]
    public void Update_text_entry_fills_display_fields()
    {
        var vm = new PreviewViewModel();
        var e = EntryFactory.Text("第一行\r\n第二行", "WeChat");
        vm.Update(e);
        Assert.Equal(PreviewMode.Text, vm.Mode);
        Assert.Equal("WeChat · 文本", vm.Title);
        Assert.Equal("W", vm.AppInitial);
        Assert.Equal("第一行\r\n第二行", vm.TextContent);
        Assert.Null(vm.ImageSource);
        Assert.Same(e, vm.CurrentEntry);
    }

    [Fact]
    public void Update_image_entry_without_loader_keeps_null_source_and_image_mode()
    {
        var vm = new PreviewViewModel();
        vm.Update(EntryFactory.Image(100, 50, 2048));
        Assert.Equal(PreviewMode.Image, vm.Mode);
        Assert.Null(vm.ImageSource); // 窗口侧以此显示「缓存已被清理」占位
    }

    [Fact]
    public void Update_invokes_loader_for_images_only()
    {
        var vm = new PreviewViewModel();
        var loaded = new List<EntryKind>();
        vm.Update(EntryFactory.Image(10, 10, 100),
            (e, _) => { loaded.Add(e.Kind); return Task.FromResult<BitmapSource?>(null); });
        Assert.True(vm.IsImageMissing); // 图像 loader 返回 null → 完成后显示占位
        vm.Update(EntryFactory.Text("x", "Notes"),
            (e, _) => { loaded.Add(e.Kind); return Task.FromResult<BitmapSource?>(null); });
        Assert.Equal(new[] { EntryKind.Image }, loaded); // loader 只对图像条目调用
        Assert.Null(vm.ImageSource);
        Assert.False(vm.IsImageMissing); // 换文本条目重置占位态
        Assert.Equal(PreviewMode.Text, vm.Mode);
    }

    [Fact]
    public void Image_load_in_progress_shows_no_placeholder()
    {
        var vm = new PreviewViewModel();
        var tcs = new TaskCompletionSource<BitmapSource?>();
        vm.Update(EntryFactory.Image(10, 10, 100), (_, _) => tcs.Task);
        // 加载中：不显示「缓存已被清理」占位（旧同步实现无法区分加载中与无图）
        Assert.False(vm.IsImageMissing);
        Assert.Null(vm.ImageSource);
        // 收尾挂起的解码：xUnit 经 SyncContext 跟踪 async void 完成数，不完成会卡死测试宿主
        tcs.SetResult(null);
    }

    [Fact]
    public void Late_image_result_does_not_apply_after_entry_switch()
    {
        var vm = new PreviewViewModel();
        var tcs = new TaskCompletionSource<BitmapSource?>();
        vm.Update(EntryFactory.Image(10, 10, 100), (_, _) => tcs.Task);
        vm.Update(EntryFactory.Text("换条目", null)); // ↑↓ 翻页离开图像条目
        tcs.SetResult(null); // 旧图解码此时才完成（无图）
        Assert.False(vm.IsImageMissing); // 旧条目的完成态不得回写到新条目
        Assert.Equal(PreviewMode.Text, vm.Mode);
    }

    // ---- 图像解码取消 / 单飞（2026-11-10 复审：连翻 ↑↓ 不再并发解码、旧结果不回写）----

    [Fact]
    public async Task Consecutive_updates_apply_only_latest_image()
    {
        var vm = new PreviewViewModel();
        var imgA = Img();
        var imgB = Img(6);
        var tcsA = new TaskCompletionSource<BitmapSource?>();
        CancellationToken tokenA = default;
        vm.Update(EntryFactory.Image(10, 10, 100), (_, ct) => { tokenA = ct; return tcsA.Task; });
        var tcsB = new TaskCompletionSource<BitmapSource?>();
        var entryB = EntryFactory.Image(20, 20, 200);
        var loaderBStarted = new TaskCompletionSource();
        vm.Update(entryB, (_, _) => { loaderBStarted.TrySetResult(); return tcsB.Task; });
        Assert.True(tokenA.IsCancellationRequested); // 换条目即取消上一张的解码
        Assert.False(loaderBStarted.Task.IsCompleted); // 单飞：第一张未收尾前不发起第二张解码

        tcsA.SetResult(imgA); // 旧解码迟到：token 已取消，不得回写
        await loaderBStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); // 第一张收尾释放门后才开始解第二张
        Assert.Null(vm.ImageSource);
        Assert.False(vm.IsImageMissing);

        tcsB.SetResult(imgB); // 单飞：第二张要等第一张收尾后才真正开始解
        await PumpUntil(() => ReferenceEquals(vm.ImageSource, imgB));
        Assert.Same(imgB, vm.ImageSource);
        Assert.Same(entryB, vm.CurrentEntry);
        Assert.False(vm.IsImageMissing);
    }

    [Fact]
    public async Task Canceled_loader_result_is_ignored()
    {
        var vm = new PreviewViewModel();
        var tcs = new TaskCompletionSource<BitmapSource?>();
        CancellationToken token = default;
        vm.Update(EntryFactory.Image(10, 10, 100), (_, ct) => { token = ct; return tcs.Task; });
        vm.CancelPendingImageLoad(); // 等价于窗口关闭/深度清理时的取消
        Assert.True(token.IsCancellationRequested);

        tcs.SetResult(Img()); // 已取消的 loader 之后才完成
        await Task.Delay(80);
        Assert.Null(vm.ImageSource);       // 不覆盖图像
        Assert.False(vm.IsImageMissing);   // 也不误标「缓存已清理」（该判断只归最新请求）
    }

    /// <summary>已冻结的小图（模拟解码产物）；尺寸仅用于区分不同实例。</summary>
    private static BitmapSource Img(int size = 4)
    {
        var pf = PixelFormats.Bgr24;
        var stride = (size * pf.BitsPerPixel + 7) / 8;
        var img = BitmapSource.Create(size, size, 96, 96, pf, null, new byte[stride * size], stride);
        img.Freeze();
        return img;
    }

    /// <summary>轮询等待 async void 回写落定（每次让出一拍，给延续回调执行机会）。</summary>
    private static async Task PumpUntil(Func<bool> predicate, int timeoutMs = 2000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(10);
    }

    // ---- 面板选中（存量 bug 回归：全新唤起不得把组头选为 SelectedItem） ----

    [Fact]
    public void Refresh_never_selects_group_header()
    {
        var engine = new HistoryEngine();
        engine.Add(ClipboardEntry.FromText("hello", "Notes"));
        var vm = new PanelViewModel(engine);
        vm.RefreshFromEngine();
        Assert.IsNotType<GroupVm>(vm.SelectedItem);
        Assert.NotNull(vm.ActiveEntry); // 全新唤起即可 Enter 粘贴 / Space 预览
    }

    // ---- 落点数学 ----

    private static readonly MonitorRect FullHd = new("\\\\.\\DISPLAY1", 0, 0, 1920, 1080, true, 96);

    [Fact]
    public void Center_left_centers_on_monitor()
    {
        Assert.Equal(600, PreviewLayout.CenterLeft(FullHd, 96, 720), 3);
        Assert.Equal(660, PreviewLayout.CenterLeft(FullHd, 96, 600), 3);
    }

    [Fact]
    public void Center_left_accounts_for_monitor_origin()
    {
        var second = new MonitorRect("\\\\.\\DISPLAY2", 1920, 0, 1920, 1080, false, 96);
        Assert.Equal(1920 + 600, PreviewLayout.CenterLeft(second, 96, 720), 3);
    }

    [Fact]
    public void Center_left_scales_by_dpi()
    {
        // 150% 屏：物理 2880×1620 → DIP 1920×1080
        var dpi150 = new MonitorRect("\\\\.\\DISPLAY1", 0, 0, 2880, 1620, true, 144);
        Assert.Equal(600, PreviewLayout.CenterLeft(dpi150, 144, 720), 3);
    }

    [Fact]
    public void Center_left_clamps_to_left_edge_when_window_wider_than_screen()
        => Assert.Equal(0, PreviewLayout.CenterLeft(new("\\\\.\\DISPLAY1", 0, 0, 800, 600, true, 96), 96, 900), 3);

    [Fact]
    public void Anchor_top_places_center_at_46_percent()
        => Assert.Equal(1080 * 0.46 - 280, PreviewLayout.AnchorTop(FullHd, 96, 560), 3);

    [Fact]
    public void Anchor_top_clamps_tall_window_into_screen()
    {
        var small = new MonitorRect("\\\\.\\DISPLAY1", 0, 0, 1024, 600, true, 96);
        Assert.Equal(0, PreviewLayout.AnchorTop(small, 96, 700), 3); // 比屏高 → 贴顶
    }

    [Fact]
    public void Clamp_top_within_pulls_overflow_back()
    {
        Assert.Equal(1080 - 560, PreviewLayout.ClampTopWithin(FullHd, 96, 900, 560), 3);
        Assert.Equal(100, PreviewLayout.ClampTopWithin(FullHd, 96, 100, 400), 3); // 屏内不动
    }

    // ---- 遮罩整屏覆盖 ----

    [Fact]
    public void Cover_rect_spans_monitor_at_96dpi()
    {
        var r = PreviewLayout.CoverRect(FullHd, 96);
        Assert.Equal((0.0, 0.0, 1920.0, 1080.0), r);
    }

    [Fact]
    public void Cover_rect_accounts_for_monitor_origin()
    {
        var second = new MonitorRect("\\\\.\\DISPLAY2", 1920, -1080, 1920, 1080, false, 96); // 主屏左上方的副屏（负坐标）
        var r = PreviewLayout.CoverRect(second, 96);
        Assert.Equal((1920.0, -1080.0, 1920.0, 1080.0), r);
    }

    [Fact]
    public void Cover_rect_scales_by_dpi()
    {
        var dpi150 = new MonitorRect("\\\\.\\DISPLAY1", 0, 0, 2880, 1620, true, 144);
        var r = PreviewLayout.CoverRect(dpi150, 144);
        Assert.Equal((0.0, 0.0, 1920.0, 1080.0), r);
    }
    // ---- 大文本显示上限（2026-11-06 性能复审③：TextBlock 全量排版可秒级冻结；
    //      2026-11-10 复审把显示上限从 256K 收紧到 32K，以单次排版成本为目标）----

    [Fact]
    public void Text_display_cap_is_32k()
    {
        Assert.Equal(32 * 1024, PreviewViewModel.MaxTextChars);
    }

    [Fact]
    public void Text_content_under_cap_passes_through_untouched()
    {
        var at = new string('x', PreviewViewModel.MaxTextChars);
        Assert.Equal(at, PreviewViewModel.BuildTextContent(EntryFactory.Text(at, "Notepad")));
    }

    [Fact]
    public void Text_over_cap_by_one_char_is_truncated()
    {
        var over = new string('x', PreviewViewModel.MaxTextChars + 1);
        var content = PreviewViewModel.BuildTextContent(EntryFactory.Text(over, "Notepad"));
        Assert.Equal(over[..PreviewViewModel.MaxTextChars], content[..PreviewViewModel.MaxTextChars]);
        Assert.Contains("照常粘贴", content);
        Assert.Contains(over.Length.ToString("N0"), content);
    }

    [Fact]
    public void Oversized_text_is_truncated_with_marker()
    {
        var big = new string('x', PreviewViewModel.MaxTextChars + 1000);
        var content = PreviewViewModel.BuildTextContent(EntryFactory.Text(big, "Notepad"));
        // 头部逐字保留到上限，尾部换成截断标注（含真实总字数）
        Assert.Equal(big[..PreviewViewModel.MaxTextChars], content[..PreviewViewModel.MaxTextChars]);
        Assert.Contains("照常粘贴", content);
        Assert.Contains(big.Length.ToString("N0"), content);
        Assert.True(content.Length < big.Length + 100);
    }
}

/// <summary>测试条目工厂（ClipboardEntry 为 init-only，重建即改值）。</summary>
public static class PreviewHelpers
{
    public static ClipboardEntry Text(string text, string? sourceApp) => ClipboardEntry.FromText(text, sourceApp);

    public static ClipboardEntry Image(int w, int h, long bytes) => ClipboardEntry.FromImage("x.png", w, h, bytes, "Shot");

    public static ClipboardEntry WithTime(ClipboardEntry e, DateTime utc)
    {
        return e.Kind switch
        {
            EntryKind.Image => new ClipboardEntry
            {
                Id = e.Id, Kind = e.Kind, Preview = e.Preview, ImageFile = e.ImageFile,
                ImageWidth = e.ImageWidth, ImageHeight = e.ImageHeight, SourceApp = e.SourceApp,
                Pinned = e.Pinned, SizeBytes = e.SizeBytes, UtcTimestamp = utc,
            },
            _ => new ClipboardEntry
            {
                Id = e.Id, Kind = e.Kind, Preview = e.Preview, FullText = e.FullText,
                SourceApp = e.SourceApp, Pinned = e.Pinned, SizeBytes = e.SizeBytes, UtcTimestamp = utc,
            },
        };
    }

    /// <summary>今天的指定时刻（本地时区）；TimeFormat 对今天输出 HH:mm，期望值可硬编码。</summary>
    public static DateTime Today(int hour, int minute) => DateTime.Today.AddHours(hour).AddMinutes(minute).ToUniversalTime();
}
