using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Pasteup.Core.Interop;

namespace Pasteup.Core.Interop;

/// <summary>
/// 专用线程上的 Win32 message-only 窗口。热键、剪贴板监听、系统设置变更都需要消息循环；
/// 每个 MessageWindow 实例独占一个线程与一个隐藏窗口，事件在该线程回调。
/// 跨线程纪律：窗口线程上订阅的事件处理器**绝不可同步等待 UI 线程**（Dispatcher.Invoke），
/// 否则与 UI 线程的 <see cref="Invoke{T}"/>（等窗口线程）互等成环——见 Invoke 的超时/取消语义。
/// </summary>
public sealed class MessageWindow : IDisposable
{
    private const uint WM_INVOKE = 0x8000; // WM_APP：唤醒消息循环以排空封送队列

    /// <summary>
    /// Invoke 等待窗口线程的上限。原实现 5s：与「消息线程 Dispatcher.Invoke 等 UI」成环时，
    /// UI 线程会被整整冻住 5s 才报错。降为 2s 后调用方更快拿到失败（超时项同时被标记取消，
    /// 不会被窗口线程恢复后补做），热键注册等调用方可走既有诊断/重试路径。
    /// </summary>
    private const int InvokeTimeoutMs = 2000;

    private static int _instanceCounter;

    private readonly string _className = $"PasteupMsg_{Environment.CurrentManagedThreadId}_{Interlocked.Increment(ref _instanceCounter)}";
    private readonly Thread _thread;
    private readonly NativeMethods.WndProc _proc;
    private readonly ConcurrentQueue<PendingInvoke> _pendingInvokes = new();
    private IntPtr _hwnd;
    private uint _threadId;        // Win32 线程 id（PostThreadMessage 用）
    private int _managedThreadId; // 托管线程 id（Invoke 内联判定用，两者不同）
    private volatile bool _disposed;

    /// <summary>封送到窗口线程执行的工作项。Canceled 由调用方在超时/释放时置位（volatile 跨线程可见）：
    /// 窗口线程排空队列时跳过它——旧实现超时后委托仍会被执行，把调用方已放弃的操作也做掉。</summary>
    private sealed class PendingInvoke(Action run)
    {
        public Action Run { get; } = run;
        private volatile bool _canceled;

        public bool Canceled
        {
            get => _canceled;
            set => _canceled = value;
        }
    }

    /// <summary>窗口句柄（线程就绪后有效）。</summary>
    public IntPtr Handle => _hwnd;

    /// <summary>收到任意消息时触发（在窗口线程上）。返回值由调用方决定——返回 IntPtr 表示已处理。</summary>
    public event Func<uint, IntPtr, IntPtr, IntPtr>? Message;

    /// <summary>WndProc 兜底 catch 吞掉的异常上报（Core 不能直接落盘日志，App 层订阅转 CrashLog；
    /// 订阅者自身异常会被吞，不得再抛）。</summary>
    public static event Action<Exception>? UnhandledWndProcException;

    public MessageWindow()
    {
        _proc = WndProc;
        var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            // 托管线程 id 与 Win32 线程 id 不是一回事：Invoke 的「是否已在窗口线程」内联判定
            // 必须用托管 id 比较（原实现拿 Environment.CurrentManagedThreadId 比 GetCurrentThreadId，
            // 窗口线程上永远不相等 → 嵌套 Invoke 自投递等自己，2000ms 才超时，2026-11-11 测试实测抓出）
            _managedThreadId = Environment.CurrentManagedThreadId;
            var wc = new NativeMethods.WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
                hInstance = NativeMethods.GetModuleHandle(null),
                lpszClassName = _className,
            };
            NativeMethods.RegisterClass(ref wc);
            _hwnd = NativeMethods.CreateWindowEx(0, _className, string.Empty, 0,
                0, 0, 0, 0, NativeMethods.HWND_MESSAGE, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            ready.Set();
            if (_hwnd == IntPtr.Zero) return;

            while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_INVOKE)
                {
                    DrainPendingInvokes();
                    continue;
                }
                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessage(ref msg);
                DrainPendingInvokes();
            }
            DrainPendingInvokes(); // WM_QUIT 后排空，避免在途调用死等
        })
        { IsBackground = true, Name = "Pasteup.MessageWindow" };
        _thread.Start();
        ready.Wait(3000);
    }

    /// <summary>封送到窗口线程同步执行。RegisterHotKey 等不接受跨线程窗口句柄，
    /// 必须在窗口所属线程上调用（2026-10-27 踩坑：跨线程调用导致热键注册恒失败）。
    /// 窗口未建成（Handle==0）或已在窗口线程上时内联执行（既有语义保留）。
    /// 超时/释放语义：等满 <see cref="InvokeTimeoutMs"/> 抛 TimeoutException，并把该工作项标记取消
    /// （窗口线程恢复后排空时跳过，不再补做已放弃的操作）；等待期间对象被 Dispose 则尽快抛
    /// ObjectDisposedException（分段轮询，不等满超时）。</summary>
    public T Invoke<T>(Func<T> func)
    {
        // 已释放：窗口线程可能已退出，投递只会白等到超时才报错——直接快速失败
        System.ObjectDisposedException.ThrowIf(_disposed, this);
        // 窗口未建成（CreateWindowEx 失败）：内联执行，调用方按 Handle 判失败（既有语义保留）
        if (_hwnd == IntPtr.Zero) return func();
        if (Environment.CurrentManagedThreadId == _managedThreadId) return func(); // 已在窗口线程：内联（防自投递死锁）

        var done = new ManualResetEventSlim(false);
        T result = default!;
        Exception? error = null;
        var item = new PendingInvoke(() =>
        {
            try { result = func(); }
            catch (Exception e) { error = e; }
            finally { done.Set(); }
        });
        _pendingInvokes.Enqueue(item);
        _ = NativeMethods.PostThreadMessage(_threadId, WM_INVOKE, IntPtr.Zero, IntPtr.Zero);

        // 分段等待：既等结果，也轮询释放标志。原实现一次 done.Wait(5000)——消息线程若正同步等 UI
        // （Dispatcher.Invoke），UI 又在本方法里等消息线程，两者互等到 5s 才解开。现在：
        // ①超时 2s 后置 Canceled，窗口线程恢复时跳过该工作项（不补做已放弃的操作）；
        // ②等待期间被 Dispose 时 50ms 内抛 ObjectDisposedException（不再干等满超时）。
        var deadline = Environment.TickCount64 + InvokeTimeoutMs;
        while (!done.Wait(50))
        {
            if (_disposed)
            {
                item.Canceled = true; // 排空时跳过（若已被取走执行则无法回收，尽力而为）
                throw new System.ObjectDisposedException(nameof(MessageWindow));
            }
            if (Environment.TickCount64 < deadline) continue;
            if (done.IsSet) break; // 临界：刚好完成，按正常结果返回
            item.Canceled = true;  // 超时放弃：排空时跳过，不再把已放弃的操作做掉
            throw new TimeoutException(
                $"MessageWindow.Invoke timed out after {InvokeTimeoutMs}ms：窗口线程未响应。");
        }
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    private void DrainPendingInvokes()
    {
        while (_pendingInvokes.TryDequeue(out var item))
        {
            // 已取消（调用方超时/释放放弃）或窗口已释放：不再执行已放弃的工作项
            if (item.Canceled || _disposed) continue;
            item.Run();
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 这里是托管→本机→托管的反向 P/Invoke 边界：异常一旦穿出，进程直接终结且无兜底机会。
        // 事件处理器抛出的异常一律就地吞掉（本次消息丢弃，后续消息/剪贴板变更仍会到来）。
        try
        {
            if (msg == NativeMethods.WM_DESTROY)
            {
                if (Message != null) _ = Message(msg, wParam, lParam);
                return IntPtr.Zero;
            }
            var result = Message?.Invoke(msg, wParam, lParam);
            return result ?? NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            // 吞掉（反向 P/Invoke 边界异常=进程终结），但上报给静态事件——消息层异常曾完全不可见
            try { UnhandledWndProcException?.Invoke(ex); }
            catch { }
            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 置位后：排队中的 Invoke 工作项在排空时被跳过（不再执行已放弃的委托），等待方在 50ms 轮询
        // 内抛 ObjectDisposedException 快速失败。DestroyWindow 只能在窗口线程调用；投递 WM_QUIT
        // 结束消息循环，线程退出时系统销毁其窗口。
        if (_threadId != 0)
            NativeMethods.PostThreadMessage(_threadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
