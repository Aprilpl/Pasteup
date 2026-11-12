using System.Diagnostics;
using Pasteup.Core.Interop;
using Xunit;

namespace Pasteup.Tests;

/// <summary>
/// MessageWindow 跨线程封送语义（第 4 批 P3-1 断环）：
/// ①值/异常原样传递、真的在窗口线程执行、窗口线程内嵌套 Invoke 内联不自投递；
/// ②Dispose 后 Invoke 抛 ObjectDisposedException（等待期间 Dispose 也快速失败，不等满超时）；
/// ③超时后排队的工作项被标记取消，窗口线程恢复/排空时不再执行（旧实现超时后仍会补做已放弃的操作）。
/// 注：「窗口未建成（Handle==0）时内联执行」的既有语义无法在单测里稳定构造 CreateWindowEx 失败，
/// 按代码保留、不在此断言。
/// </summary>
public class MessageWindowInvokeTests
{
    [Fact]
    public void Invoke_returns_value_and_runs_on_message_thread()
    {
        using var window = new MessageWindow();
        Assert.True(window.Handle != IntPtr.Zero, "message-only 窗口未建成，测试环境异常");
        var callerThread = Environment.CurrentManagedThreadId;

        Assert.Equal(42, window.Invoke(() => 42));
        // 证明真的封送到专属窗口线程执行（而不是在调用线程内联）
        Assert.NotEqual(callerThread, window.Invoke(() => Environment.CurrentManagedThreadId));
    }

    [Fact]
    public void Invoke_propagates_exception_and_window_stays_usable()
    {
        using var window = new MessageWindow();
        var ex = Assert.Throws<InvalidOperationException>(
            () => { _ = window.Invoke<int>(() => throw new InvalidOperationException("boom")); });
        Assert.Equal("boom", ex.Message);

        // 异常经封送原样抛回，后续 Invoke 不受污染
        Assert.Equal(5, window.Invoke(() => 5));
    }

    [Fact]
    public async Task Invoke_on_window_thread_is_inline_not_self_deadlock()
    {
        using var window = new MessageWindow();
        var nested = Task.Run(() => window.Invoke(() => window.Invoke(() => 7)));

        // 若内联分支缺失会自投递等自己而死锁：5s 兜底只为防挂测试宿主，不改变断言语义
        var completed = await Task.WhenAny(nested, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(nested, completed);
        Assert.Equal(7, await nested);
    }

    [Fact]
    public void Invoke_after_dispose_throws_object_disposed()
    {
        var window = new MessageWindow();
        Assert.True(window.Handle != IntPtr.Zero, "message-only 窗口未建成，测试环境异常");
        window.Dispose();
        window.Dispose(); // 幂等

        Assert.Throws<ObjectDisposedException>(() => { _ = window.Invoke(() => 1); });
    }

    [Fact]
    public async Task Timeout_cancels_pending_item_so_it_never_runs()
    {
        using var window = new MessageWindow();
        Assert.True(window.Handle != IntPtr.Zero, "message-only 窗口未建成，测试环境异常");
        using var started = new ManualResetEventSlim(false);
        using var unblock = new ManualResetEventSlim(false);

        // 占住窗口线程（模拟「窗口线程未响应」）：该 Invoke 的委托在窗口线程上阻塞，其调用方
        // 因超过 InvokeTimeoutMs 而超时返回（预期失败——委托已在执行，取消收不回来）。
        var blocker = Task.Run(() =>
        {
            try
            {
                _ = window.Invoke(() =>
                {
                    started.Set();
                    unblock.Wait(TimeSpan.FromSeconds(10));
                    return 0;
                });
            }
            catch (TimeoutException)
            {
                // 预期：阻塞委托超过 Invoke 超时，调用方放弃
            }
        });
        var spin = Stopwatch.StartNew();
        while (!started.IsSet && spin.ElapsedMilliseconds < 5000) await Task.Delay(10);
        Assert.True(started.IsSet, "窗口线程未开始执行阻塞委托");

        // 窗口线程仍被占住：第二个 Invoke 等满超时后抛 TimeoutException，并把工作项标记取消
        var ran = 0;
        Assert.Throws<TimeoutException>(() =>
        {
            _ = window.Invoke(() => { ran = 1; return 1; });
        });

        // 放行窗口线程：排空队列时必须跳过已取消的工作项（旧实现会把它照做掉）
        unblock.Set();
        var recovered = await Task.WhenAny(blocker, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(blocker, recovered);
        await blocker;

        // 正常 Invoke 作同步点：它完成时队列已排空 → 被取消的项确实没有执行过
        Assert.Equal(7, window.Invoke(() => 7));
        Assert.Equal(0, Volatile.Read(ref ran));
    }

    [Fact]
    public async Task Dispose_during_wait_fails_fast_before_timeout()
    {
        var window = new MessageWindow();
        using var started = new ManualResetEventSlim(false);
        using var unblock = new ManualResetEventSlim(false);
        var blocker = Task.Run(() => window.Invoke(() =>
        {
            started.Set();
            unblock.Wait(TimeSpan.FromSeconds(10));
            return 0;
        }));
        try
        {
            var spin = Stopwatch.StartNew();
            while (!started.IsSet && spin.ElapsedMilliseconds < 5000) await Task.Delay(10);
            Assert.True(started.IsSet, "窗口线程未开始执行阻塞委托");

            var queued = Task.Run(() => window.Invoke(() => 1));
            await Task.Delay(100); // 让排队方进入 Invoke 等待（入队/投递之后才谈得上取消）
            var sw = Stopwatch.StartNew();
            window.Dispose(); // 置 _disposed：等待方应在 50ms 轮询内抛 ODE，而不是干等 2s 超时

            var done = await Task.WhenAny(queued, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.Same(queued, done);
            var error = await Record.ExceptionAsync(() => queued);
            Assert.IsType<ObjectDisposedException>(error);
            Assert.True(sw.ElapsedMilliseconds < 1500,
                $"Dispose 后排队 Invoke 未快速失败：{sw.ElapsedMilliseconds}ms");
        }
        finally
        {
            unblock.Set();
            _ = await Task.WhenAny(blocker, Task.Delay(TimeSpan.FromSeconds(5)));
            window.Dispose();
        }
    }
}
