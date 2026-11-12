using System.Runtime.InteropServices;

namespace Pasteup.App.Services;

/// <summary>
/// caret 探针线程（2026-11-10 落地；2026-11-10 复审问题1 升级为**全链 caret 探针线程**）。
///
/// 为什么整条链都要放在这一条专用后台 STA 线程上：
/// · UIA 与 MSAA 都是**跨进程 COM 调用**，目标应用卡死时调用可能长时间不返回——放在 WPF UI 线程
///   上就是整窗冻结（复审问题1 的「已 Show 却白块」），放在线程池上则阻塞池线程，且 MSAA 在 MTA
///   上与 UIA 的 STA 上下文不一致（两条 COM 链应在同一套 STA 上下文里串行，行为才可预期）；
/// · UIA 客户端进程内只建一次（启动空闲期 <see cref="WarmUp"/> 预热），热查询实测 4–7ms；
/// · 调用方（UI 线程）只 await，不占 UI 线程、也不占线程池；单次最多等 <c>timeoutMs</c>，
///   超时即放弃（本次退回鼠标）。工作线程仍在目标进程里跑时 <c>_busy</c> 保持 true，后续请求
///   直接 `busy-skip` 不排队堆积——宁可退化成鼠标锚点，绝不拖住唤起。
///
/// 两条入口共用同一条线程与同一个 <c>_busy</c> 互斥（避免同步/异步请求交错）：
/// · <see cref="ProbeAsync"/>：异步全链 UIA→MSAA，尺寸/焦点控件/在屏校验全部复用
///   <see cref="CaretAcquisition.TryAcquire"/>，不重写；
/// · <see cref="TryGet"/>：既有同步 UIA-only 查询（供 <see cref="UiaCaretProbe"/> / `--poc=caret` /
///   <see cref="CaretAcquisition.DefaultProbes"/> 使用），行为与线程语义保持不变。
/// </summary>
internal sealed class UiaCaretProbeThread
{
    public static UiaCaretProbeThread Shared { get; } = new();

    /// <summary>同步 UIA-only 查询的等待上限（毫秒）。Chromium 首次激活无障碍树可能超过它——那一次
    /// 退回鼠标，树建好后的下一次唤起即命中（实测热查询 4–7ms）。</summary>
    internal const int QueryTimeoutMs = 150;

    /// <summary>异步全链（UIA→MSAA）默认等待上限（毫秒）。</summary>
    internal const int AsyncProbeTimeoutMs = 200;

    private readonly object _gate = new();
    private Thread? _thread;
    private ManualResetEventSlim? _ready;
    private AutoResetEvent? _request;
    private ManualResetEventSlim? _done;
    private volatile bool _busy;
    private volatile bool _initFailed;
    private string _initError = "";
    private IntPtr _uia;

    /// <summary>线程内探针链（UIA 直连本 STA 线程的客户端 + MSAA）；创建后只在工作线程上访问。</summary>
    private ICaretProbe[]? _probeChain;

    /// <summary>待工作线程消费的请求（同步或异步），由 <see cref="_gate"/> 保护。</summary>
    private ProbeRequest? _current;

    // ---- 同步 TryGet 的结果槽：工作线程写、调用方读，都在 _gate 内 ----
    private bool _hasResult;
    private Win32Input.PhysicalRect _rect;
    private string _detail = "";

    /// <summary>一次探针请求。同步请求 = UIA-only 原始结果（写回 <see cref="_rect"/>/<see cref="_detail"/>）；
    /// 异步请求 = 全链结果经 <see cref="Completion"/> 交回。</summary>
    private sealed class ProbeRequest
    {
        public bool Async { get; init; }
        public CaretProbeContext? Context { get; init; }
        public IntPtr ForegroundWindow { get; init; }
        public int ForegroundPid { get; init; }
        public TaskCompletionSource<(CaretHit? Hit, string Trace)>? Completion { get; init; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax,
        uint removeMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    private UiaCaretProbeThread()
    {
    }

    /// <summary>启动线程并创建 UIA 客户端（不等就绪）：启动空闲期调用，把首次冷启动成本提前付掉。</summary>
    public void WarmUp() => _ = EnsureStarted(0, out _);

    /// <summary>
    /// 既有同步入口：只问 UIA（原始矩形，不做采集链校验），最多等 <see cref="QueryTimeoutMs"/>。
    /// 工作线程忙时直接 `busy-skip`。行为与线程语义跟合并前一致——只把「请求的投递/回收」换成了
    /// 统一的 <see cref="ProbeRequest"/>，仍由同一条 STA 线程执行。
    /// </summary>
    public bool TryGet(IntPtr foregroundWindow, out Win32Input.PhysicalRect caret, out string why)
    {
        caret = default;
        if (foregroundWindow == IntPtr.Zero)
        {
            why = "no-foreground";
            return false;
        }
        if (!EnsureStarted(QueryTimeoutMs, out why)) return false;
        if (_busy)
        {
            why = "busy-skip";
            return false;
        }

        lock (_gate)
        {
            if (_busy)
            {
                why = "busy-skip";
                return false;
            }
            _busy = true;
            _current = new ProbeRequest
            {
                Async = false,
                ForegroundWindow = foregroundWindow,
                ForegroundPid = (int)Win32Input.ProcessIdOfWindow(foregroundWindow),
            };
            _done!.Reset();
        }

        _request!.Set();
        if (!_done!.Wait(QueryTimeoutMs))
        {
            // 工作线程仍在目标进程里阻塞：_busy 保持 true，后续请求直接 busy-skip 直到它返回
            why = $"timeout-{QueryTimeoutMs}ms";
            return false;
        }

        lock (_gate)
        {
            caret = _rect;
            why = _detail;
            return _hasResult;
        }
    }

    /// <summary>
    /// 异步全链探针（UIA TextPattern → MSAA）：在**本线程的 STA 上下文**里跑完整采集链
    /// （复用 <see cref="CaretAcquisition.TryAcquire"/> 的尺寸/焦点控件/在屏三层校验与逐级回退）。
    /// 命中返回 <see cref="CaretHit"/>；失败/超时/忙/未就绪只返回 null + trace（不抛异常，调用方
    /// 据此回退鼠标锚点）。超时后工作线程仍会把结果写进 Task——只 SetResult 不 SetException，
    /// 无人 await 也不产生未观察异常。
    /// </summary>
    public async Task<(CaretHit? Hit, string Trace)> ProbeAsync(CaretProbeContext context,
        int timeoutMs = AsyncProbeTimeoutMs)
    {
        // 启动预热尚未就绪时不等待（本方法可能被 UI 线程调用；冷启动竞态下宁可退回鼠标，
        // 首次等待就绪的 100+ms 正是复审问题1 要消灭的阻塞）
        if (!EnsureStarted(0, out var notReady)) return (null, notReady);

        var completion = new TaskCompletionSource<(CaretHit? Hit, string Trace)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_busy) return (null, "busy-skip");
            _busy = true;
            _current = new ProbeRequest
            {
                Async = true,
                Context = context,
                Completion = completion,
            };
        }

        _request!.Set();
        var timeoutTask = Task.Delay(timeoutMs);
        if (ReferenceEquals(await Task.WhenAny(completion.Task, timeoutTask), timeoutTask))
        {
            // 超时：工作线程可能仍卡在目标进程里——_busy 保持 true，后续请求 busy-skip 直到它返回。
            // 这里不能取消（COM 调用无取消机制），也不要把它的 Task 标异常。
            return (null, $"timeout-{timeoutMs}ms");
        }
        return await completion.Task;
    }

    private bool EnsureStarted(int waitReadyMs, out string why)
    {
        why = "";
        ManualResetEventSlim ready;
        lock (_gate)
        {
            if (_thread is null)
            {
                _ready = new ManualResetEventSlim(false);
                _request = new AutoResetEvent(false);
                _done = new ManualResetEventSlim(false);
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "pasteup-caret-probe",
                };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
            }
            ready = _ready!;
        }

        if (waitReadyMs > 0 && !ready.Wait(waitReadyMs))
        {
            why = "uia-not-ready";
            return false;
        }
        if (!ready.IsSet)
        {
            why = "uia-not-ready";
            return false;
        }
        if (_initFailed)
        {
            why = "uia-init-failed:" + _initError;
            return false;
        }
        return true;
    }

    private void Run()
    {
        UiaTextCaret.InitializeCom();
        try
        {
            if (UiaTextCaret.TryCreateClient(out var uia, out var error))
            {
                _uia = uia;
                // 线程内探针链：UIA 必须直连本线程创建的客户端。严禁在这里再调
                // UiaCaretProbeThread.Shared.TryGet——那是向自己发请求，会 busy-skip 或自等待；
                // MSAA 复用既有实现（入口自带 IsHungAppWindow 快检）。
                _probeChain = new ICaretProbe[] { new ThreadUiaCaretProbe(uia), new MsaaCaretProbe() };
            }
            else
            {
                _initFailed = true;
                _initError = error;
            }
            _ready!.Set();
            if (_initFailed) return;

            while (true)
            {
                if (_request!.WaitOne(100))
                {
                    ProbeRequest? request;
                    lock (_gate)
                    {
                        request = _current;
                        _current = null;
                    }
                    if (request is not null)
                    {
                        if (request.Async) RunAsyncProbe(request);
                        else RunSyncProbe(request);
                    }
                }
                PumpThreadMessages();
            }
        }
        finally
        {
            UiaTextCaret.UninitializeCom();
        }
    }

    /// <summary>同步请求：UIA 原始读取（不做校验链），结果写回结果槽后唤醒调用方。</summary>
    private void RunSyncProbe(ProbeRequest request)
    {
        bool ok;
        Win32Input.PhysicalRect rect = default;
        string detail;
        try
        {
            ok = UiaTextCaret.TryReadCaret(_uia, request.ForegroundWindow, request.ForegroundPid, out rect,
                out detail);
        }
        catch (Exception ex)
        {
            ok = false;
            rect = default;
            detail = "exception:" + ex.GetType().Name + ":" + ex.Message;
        }

        lock (_gate)
        {
            _hasResult = ok;
            _rect = rect;
            _detail = detail;
            _busy = false;
        }
        _done!.Set();
    }

    /// <summary>异步请求：在 STA 线程内跑完整 UIA→MSAA 采集链，先复位 busy 再交结果。</summary>
    private void RunAsyncProbe(ProbeRequest request)
    {
        CaretHit? hit;
        string trace;
        try
        {
            hit = CaretAcquisition.TryAcquire(request.Context!, _probeChain!, out trace);
        }
        catch (Exception ex)
        {
            // 采集链内部已按探针隔离异常，这里只是兜底：任何意外都降级为「没取到」
            hit = null;
            trace = "exception:" + ex.GetType().Name + ":" + ex.Message;
        }

        // 先复位 busy 再 SetResult：调用方的续体可能在拿到结果后立刻发起下一次请求
        lock (_gate)
        {
            _busy = false;
        }
        request.Completion!.TrySetResult((hit, trace));
    }

    /// <summary>线程内 UIA 探针：直接调用本 STA 线程创建的 UIA 客户端（不自请求），
    /// pid 在调用时用 <see cref="Win32Input.ProcessIdOfWindow"/> 现取，避免用过期的前台进程。</summary>
    private sealed class ThreadUiaCaretProbe : ICaretProbe
    {
        private readonly IntPtr _uia;

        public ThreadUiaCaretProbe(IntPtr uia) => _uia = uia;

        public CaretSource Source => CaretSource.Uia;

        public bool TryGet(IntPtr foregroundWindow, out Win32Input.PhysicalRect caret, out string why)
        {
            caret = default;
            if (foregroundWindow == IntPtr.Zero)
            {
                why = "no-foreground";
                return false;
            }
            var pid = (int)Win32Input.ProcessIdOfWindow(foregroundWindow);
            return UiaTextCaret.TryReadCaret(_uia, foregroundWindow, pid, out caret, out why);
        }
    }

    /// <summary>泵线程消息：COM 跨线程回调（UIA/oleacc 代理）可能依赖消息队列。</summary>
    private static void PumpThreadMessages()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, 0))
        {
            if (msg.message == 0x0012 /*WM_QUIT*/) return;
            _ = TranslateMessage(ref msg);
            _ = DispatchMessage(ref msg);
        }
    }
}
