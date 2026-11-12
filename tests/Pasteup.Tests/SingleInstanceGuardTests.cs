using Pasteup.App.Services;
using Xunit;

namespace Pasteup.Tests;

/// <summary>单实例守卫：互斥体管所有权、事件管信号，名称按数据目录派生（2026-11-02 安全加固）；
/// 2026-11-10 P3-2：命名对象被同会话进程抢占时降级不崩溃（降级分支需敌意进程，靠代码审查）。</summary>
public class SingleInstanceGuardTests : IDisposable
{
    private readonly List<SingleInstanceGuard> _guards = [];

    private SingleInstanceGuard NewGuard(string dataDir)
    {
        var g = new SingleInstanceGuard(dataDir);
        _guards.Add(g);
        return g;
    }

    public void Dispose()
    {
        foreach (var g in _guards) g.Dispose();
    }

    [Fact]
    public void 同数据目录_第二实例判定为非首()
    {
        var dir = @"C:\pasteup-test\" + Guid.NewGuid().ToString("N");
        Assert.True(NewGuard(dir).IsFirstInstance);
        Assert.False(NewGuard(dir).IsFirstInstance);
    }

    [Fact]
    public void 不同数据目录_互不干扰并行皆首()
    {
        Assert.True(NewGuard(@"C:\pasteup-a-" + Guid.NewGuid().ToString("N")).IsFirstInstance);
        Assert.True(NewGuard(@"C:\pasteup-b-" + Guid.NewGuid().ToString("N")).IsFirstInstance);
    }

    [Fact]
    public void 首实例释放后_后来者可接任()
    {
        var dir = @"C:\pasteup-test\" + Guid.NewGuid().ToString("N");
        var first = NewGuard(dir);
        Assert.True(first.IsFirstInstance);
        first.Dispose();
        // 正常路径：互斥体对象随最后一个句柄关闭而销毁 → 后来者 created=true；
        // 若仍有残留句柄（异常路径）则走 WaitOne(0)/AbandonedMutexException 接管分支——两者都必须是首实例
        Assert.True(NewGuard(dir).IsFirstInstance);
    }

    [Fact]
    public void 首实例存活期间_后建守卫始终非首()
    {
        var dir = @"C:\pasteup-test\" + Guid.NewGuid().ToString("N");
        Assert.True(NewGuard(dir).IsFirstInstance);
        Assert.False(NewGuard(dir).IsFirstInstance);
        Assert.False(NewGuard(dir).IsFirstInstance);
    }

    [Fact]
    public void 唤起信号_送达首实例回调()
    {
        var dir = @"C:\pasteup-test\" + Guid.NewGuid().ToString("N");
        var first = NewGuard(dir);
        var fired = new ManualResetEventSlim();
        first.ShowRequested += () => fired.Set();

        NewGuard(dir).SignalShow(); // 第二实例转发信号

        Assert.True(fired.Wait(TimeSpan.FromSeconds(2)), "2 秒内未收到唤起信号");
    }

    [Fact]
    public void 第二实例_信号转发与释放均安全()
    {
        var dir = @"C:\pasteup-test\" + Guid.NewGuid().ToString("N");
        var first = NewGuard(dir);
        Assert.True(first.IsFirstInstance);
        var fired = new ManualResetEventSlim();
        first.ShowRequested += () => fired.Set();

        var second = NewGuard(dir);
        Assert.False(second.IsFirstInstance);
        // 事件可用时第二实例转发信号：不得抛、且首实例在 2 秒内收到
        Assert.Null(Record.Exception(() => second.SignalShow()));
        Assert.True(fired.Wait(TimeSpan.FromSeconds(2)), "2 秒内未收到唤起信号");
        // 第二实例不持有互斥体：Dispose 不得 Release（会对非拥有者抛 ApplicationException）
        Assert.Null(Record.Exception(second.Dispose));

        first.Dispose();
        Assert.Null(Record.Exception(first.Dispose)); // Dispose 幂等
    }

    [Fact]
    public void 正常路径_不降级且诊断字段为空()
    {
        var g = NewGuard(@"C:\pasteup-test\" + Guid.NewGuid().ToString("N"));
        Assert.True(g.IsFirstInstance);
        Assert.False(g.SingleInstanceDegraded);
        Assert.Null(g.DegradedReason);
        // 降级分支（同会话进程用受限 DACL 抢占 Local\ 命名对象）无法在单测里可靠模拟（需敌意进程），
        // 靠代码审查保证：Mutex/EventWaitHandle 的创建与打开都已 catch
        // UnauthorizedAccessException / WaitHandleCannotBeOpenedException / IOException；
        // 降级时 IsFirstInstance=true、SingleInstanceDegraded=true、DegradedReason=异常类型名，且不起信号线程。
    }

    [Fact]
    public void Dispose_重复调用不抛()
    {
        var g = NewGuard(@"C:\pasteup-test\" + Guid.NewGuid().ToString("N"));
        g.Dispose();
        Assert.Null(Record.Exception(g.Dispose));
    }
}
