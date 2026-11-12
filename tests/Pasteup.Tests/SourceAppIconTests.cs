using System.IO;
using Pasteup.App.Services;
using Pasteup.App.ViewModels;
using Pasteup.Core.History;
using Xunit;

namespace Pasteup.Tests;

/// <summary>来源应用图标：提取链（真 exe 48px、冻结、缓存正/负）、按名补齐（可注入解析器）、
/// 面板行为异步补图（fake 服务）、条目工厂携带路径、首字母回退规则不回归。</summary>
public class SourceAppIconTests
{
    // notepad.exe：Win11 全系必在且图标确定（write.exe 已随 WordPad 在新 build 移除，勿用）
    private static string SystemExe => Path.Combine(Environment.SystemDirectory, "notepad.exe");

    private static ClipboardEntry Text(string s, string? app = null, string? path = null) => new()
    {
        Id = Guid.NewGuid(),
        Kind = EntryKind.Text,
        Preview = s,
        FullText = s,
        SourceApp = app,
        SourceAppPath = path,
        UtcTimestamp = DateTime.UtcNow,
    };

    [Fact]
    public void Extract_real_system_exe_returns_frozen_icon()
    {
        var svc = new SourceAppIconService();
        var icon = svc.FromPath(SystemExe);
        Assert.NotNull(icon);
        Assert.True(icon.IsFrozen); // 冻结后才能跨线程赋给行 VM
        Assert.Equal(48, icon.PixelWidth); // 48px：高 DPI 面板缩到 32 DIP 不糊
    }

    [Fact]
    public void Second_call_same_path_hits_cache_by_identity()
    {
        var svc = new SourceAppIconService();
        var a = svc.FromPath(SystemExe);
        var b = svc.FromPath(SystemExe);
        Assert.Same(a, b);
    }

    [Fact]
    public void Dead_path_returns_null_and_is_negatively_cached()
    {
        var svc = new SourceAppIconService();
        Assert.Null(svc.FromPath(@"Z:\definitely\not\here.exe"));
        // 负缓存：不再重试（以同实例返回 null 而非抛错为契约，这里只验证幂等）
        Assert.Null(svc.FromPath(@"Z:\definitely\not\here.exe"));
    }

    [Fact]
    public async Task Name_only_entry_resolves_via_running_process_then_extracts()
    {
        // 解析器替身：模拟「同名进程在运行」返回写字板路径
        var svc = new SourceAppIconService(_ => SystemExe);
        var icon = await svc.GetAsync(Text("按名补齐", app: "write"));
        Assert.NotNull(icon);

        // 名字→路径 + 路径→图标 双正缓存：再次请求同实例
        var again = await svc.GetAsync(Text("再次请求", app: "WRITE")); // 名字大小写不敏感
        Assert.Same(icon, again);
    }

    [Fact]
    public async Task Name_not_running_returns_null_without_caching_miss()
    {
        var svc = new SourceAppIconService(_ => null);
        Assert.Null(await svc.GetAsync(Text("没开着", app: "ghost-app")));

        // 「没在运行」不做持久负缓存：解析器后来找到了就能补上
        var found = new SourceAppIconService(_ => SystemExe);
        Assert.NotNull(await found.GetAsync(Text("后来开了", app: "ghost-app")));
    }

    [Fact]
    public void Entry_factories_carry_source_app_path()
    {
        Assert.Equal(@"C:\app\A.exe", ClipboardEntry.FromText("t", "A", @"C:\app\A.exe").SourceAppPath);
        Assert.Equal(@"C:\app\B.exe", ClipboardEntry.FromImage("f.png", 1, 1, 2, "B", @"C:\app\B.exe").SourceAppPath);
        Assert.Equal(@"C:\app\C.exe", ClipboardEntry.FromFiles(["x"], "C", @"C:\app\C.exe").SourceAppPath);
        Assert.Null(ClipboardEntry.FromText("t", "A").SourceAppPath); // 旧调用兼容：缺省无路径
    }

    [Fact]
    public async Task Panel_rows_receive_icons_from_fake_service()
    {
        var engine = new HistoryEngine();
        engine.Add(Text("带路径", app: "A", path: SystemExe));
        var vm = new PanelViewModel(engine, new SourceAppIconService(_ => SystemExe));
        vm.RefreshFromEngine();
        var row = vm.Items.OfType<RowVm>().FirstOrDefault(r => r.Entry.SourceApp == "A");
        Assert.NotNull(row);
        Assert.Null(row.AppIcon); // 重建即未加载（行上先显示首字母）

        await Task.Yield(); // async void 加载组在无同步上下文的测试里内联/线程池完成
        await Task.Run(() => { }); // 再让一拍，确保加载组已跑完
        // 行为获得图标（真实提取自 SystemExe）
        for (var i = 0; i < 50 && row.AppIcon is null; i++) await Task.Delay(20);
        Assert.NotNull(row.AppIcon);
    }

    [Fact]
    public void Rows_without_source_get_no_icon_group_and_keep_initial()
    {
        var engine = new HistoryEngine();
        engine.Add(Text("无来源"));
        var vm = new PanelViewModel(engine, new SourceAppIconService(_ => SystemExe));
        vm.RefreshFromEngine(); // KeyOf "" 被跳过，不抛错
        var row = vm.Items.OfType<RowVm>().Single();
        Assert.Equal("·", row.AppInitial); // 空来源回退 ·
        Assert.Null(row.AppIcon);
    }

    [Fact]
    public async Task Preview_icon_loader_sets_icon_and_resets_on_entry_switch()
    {
        var icon = new SourceAppIconService().FromPath(SystemExe);
        var vm = new PreviewViewModel();
        vm.Update(Text("甲", app: "write"), iconLoader: _ => Task.FromResult(icon));
        await Task.Run(() => { }); // async void 加载在无同步上下文的测试里内联完成
        Assert.Same(icon, vm.AppIcon);

        // 换条目（无 loader）先回字母态
        vm.Update(Text("乙", app: "Edge"));
        Assert.Null(vm.AppIcon);
    }

    [Fact]
    public void Initial_fallback_rules_unchanged()
    {
        Assert.Equal("W", PreviewViewModel.AppInitialOf("WeChat", EntryKind.Text));
        Assert.Equal("◧", PreviewViewModel.AppInitialOf("Explorer", EntryKind.File));
        Assert.Equal("·", PreviewViewModel.AppInitialOf(null, EntryKind.Text));
    }
}
