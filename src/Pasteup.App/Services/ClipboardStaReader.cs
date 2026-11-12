using System.Runtime.InteropServices;
using System.Windows;
using Pasteup.Core.Clipboard;

namespace Pasteup.App.Services;

/// <summary>
/// 专用剪贴板读取线程（2026-11-10 复审问题4 第2批）：把整段 OLE 剪贴板读取从 WPF UI 线程搬走。
///
/// 为什么需要它：
/// · 跨进程 OLE 剪贴板读取（GetText/GetData/GetImage）**自身没有超时**——剪贴板 owner（任意进程，
///   包括恶意/僵死进程）不响应 WM_RENDERFORMAT 时调用会长时间不返回，放在 UI 线程上就是整窗冻结
///   （原实现即如此）；本类用一条专用后台 STA 线程承载所有读取，UI 线程只 await，最多等 timeoutMs，
///   超时降级为「本轮无内容」，下一次剪贴板变更再取。
/// · 读取线程必须是 STA 且自带消息泵：WPF 的 Clipboard（PresentationCore 的 OleServicesContext）
///   要求 STA 并在本线程首次调用时做线程级 OLE 初始化；OLE 的跨进程渲染/回调消息也由本线程
///   消息泵派发（照抄 UiaCaretProbeThread 的 PeekMessage/TranslateMessage/DispatchMessage 模式）。
/// · 忙时 busy-skip：超时后工作线程往往仍卡在 OLE 调用里，<c>_busy</c> 保持 true 直到它真正返回；
///   期间的请求立即返回 null（**不排队堆积**，不产生「超时一次、后面全超时」的雪崩），宁可少记
///   一轮也不拖住复制/唤起路径（与 UiaCaretProbeThread 的取舍一致）。
/// · 只 TrySetResult、绝不 SetException：调用方超时后已放弃该 Task；工作线程的意外异常只降级为
///   「本次读取无内容」，不产生未观察异常。
///
/// Dispose 时序（DI 在 App.OnExit 的 `_services.Dispose()` 里调用）：置 <c>_disposed</c> →
/// <c>PostThreadMessage(WM_QUIT)</c> 唤醒消息泵 → <c>Join(300)</c>。Join 有上限且不抛：若工作线程
/// 正卡在 OLE 调用里没回来，Dispose 最多等 300ms 后返回——线程是 IsBackground，不会阻塞进程退出；
/// 已排队/在途请求由调用方的超时降级为 null，不会挂死。未启动过 / 重复 Dispose 均为无害空操作。
///
/// 跨线程数据：读出的 string / byte[] / List&lt;string&gt; 天然可跨线程；DIB 位图在**读取线程上**
/// <c>Freeze()</c> 后再交回（WPF Freezable 冻结后即可跨线程），随后的解码校验/编码/落盘在
/// Task.Run 后台段继续做。读取失败一律返回 null，异常绝不穿出线程。
///
/// 退订格式（2026-11-10 C1-A）：每个读取请求在真正读内容之前先查「勿记录/勿上云」标记
///（<see cref="ClipboardCapturePolicy"/>：Windows 剪贴板历史/云剪贴板 + 密码管理器约定格式），
/// 命中即当本轮无内容、整条载荷不入库——隐私优先；检查失败按不跳过（可用性优先）。
/// </summary>
public sealed class ClipboardStaReader : IDisposable
{
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;
    private const uint PmRemove = 0x0001;

    /// <summary>请求槽/线程状态互斥：_busy、_current、_thread、_request、_disposed 的写入都在锁内。</summary>
    private readonly object _gate = new();

    private Thread? _thread;
    private AutoResetEvent? _request;
    private ReadRequest? _current;
    private bool _busy;             // 工作线程有在途读取（_gate 保护）：true 期间新请求 busy-skip
    private volatile bool _disposed;
    private volatile int _threadId; // 仅供 Dispose 发 WM_QUIT（0 = 线程消息队列尚未就绪）

    /// <summary>一次读取请求（单槽，不排队）：<c>DibOnly</c> 只用于「PNG 字节损坏 → 回退 DIB」路径。</summary>
    private sealed class ReadRequest(bool dibOnly)
    {
        public bool DibOnly { get; } = dibOnly;

        /// <summary>结果交回点：RunContinuationsAsynchronously 保证续体不内联在工作线程上执行。</summary>
        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>读取当前剪贴板载荷：文本串 / "PNG" 字节 / Freeze 后的 DIB 位图 / 文件列表。
    /// 无有效内容、剪贴板忙、线程未就绪或等待超时一律返回 null（调用方按「本轮无内容」处理，
    /// 下轮剪贴板变更再取）。超时**不代表**工作线程停止：它仍在 OLE 调用里，_busy 保持 true
    /// 直到返回，随后的请求直接 busy-skip。</summary>
    public Task<object?> ReadAsync(int timeoutMs = 300) => ReadCore(dibOnly: false, timeoutMs);

    /// <summary>只读 DIB 载荷（PNG 字节损坏时 IngestClipboardAsync 的回退路径，语义同原
    /// <c>ReadDibPayload</c>：优先从 CF_DIB 解出位图，PNG 通道被换格式投毒时仍能入库）。</summary>
    internal Task<object?> ReadDibAsync(int timeoutMs = 300) => ReadCore(dibOnly: true, timeoutMs);

    private async Task<object?> ReadCore(bool dibOnly, int timeoutMs)
    {
        var completion = Enqueue(dibOnly);
        if (completion is null) return null; // 已释放/忙/线程起不来：立即降级，不排队

        // ConfigureAwait(false)：本方法内部**不得**依赖 UI 线程泵。--poc=clipread 会在 UI 线程上
        // 用 GetResult() 同步等本 Task；若续体被拉回 UI 线程就等于互相等死锁。调用方自己的
        // await 仍按默认捕获上下文回原线程（IngestClipboardAsync 的后续段仍在 UI 线程）。
        var delay = Task.Delay(Math.Max(0, timeoutMs));
        if (!ReferenceEquals(await Task.WhenAny(completion.Task, delay).ConfigureAwait(false), completion.Task))
            return null; // 超时：工作线程可能仍卡在 OLE 调用里（_busy 保留，由它返回时复位）

        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>把请求投进单槽并唤醒工作线程；忙/已释放返回 null（busy-skip 语义）。</summary>
    private TaskCompletionSource<object?>? Enqueue(bool dibOnly)
    {
        if (!EnsureStarted()) return null;
        ReadRequest request;
        lock (_gate)
        {
            if (_disposed || _busy) return null;
            _busy = true;
            request = new ReadRequest(dibOnly);
            _current = request;
        }
        _request!.Set();
        return request.Completion;
    }

    /// <summary>懒启动读取线程（首次请求时）；已释放返回 false。</summary>
    private bool EnsureStarted()
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (_thread is not null) return true;
            _request = new AutoResetEvent(false);
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "pasteup-clipboard-read",
            };
            _thread.SetApartmentState(ApartmentState.STA); // OLE 剪贴板要求 STA（WPF Clipboard 会校验）
            _thread.Start();
            return true;
        }
    }

    private void Run()
    {
        _threadId = (int)GetCurrentThreadId();
        // 先建本线程消息队列（PeekMessage 的副作用）：Dispose 的 PostThreadMessage(WM_QUIT) 与 OLE
        // 跨进程回调都要求队列已存在；PM_NOREMOVE 不消费消息
        _ = PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove);
        try
        {
            while (!_disposed)
            {
                if (_request!.WaitOne(100)) // 100ms 轮询：让 Dispose 的 _disposed 最迟一拍生效
                {
                    ReadRequest? request;
                    lock (_gate)
                    {
                        request = _current;
                        _current = null;
                        // Dispose 可能已把槽里的请求丢弃：复位 busy，保持状态一致（此后不再接受新请求）
                        if (request is null) _busy = false;
                    }
                    if (request is not null)
                    {
                        object? payload;
                        try
                        {
                            // 退订格式检查（C1-A）：命中「勿记录/勿上云」标记 → 本轮整条载荷视为无内容。
                            // 必须在读任何内容之前判（文本/PNG/DIB/文件列表/坏 PNG 回退 DIB 全覆盖）；
                            // 策略自身已吞掉检查异常，这里再兜底一次（OOM 等），绝不让异常穿出读取线程
                            payload = IsExcludedByPolicy()
                                ? null
                                : request.DibOnly ? ReadDibPayload() : ReadClipboardPayload();
                        }
                        catch (Exception)
                        {
                            // 读取线程里的任何意外都降级为「本次无内容」：异常不许穿出线程
                            // （后台线程未处理异常会终止整个进程）
                            payload = null;
                        }
                        // 先复位 _busy 再交结果：等待方的续体可能立刻发起下一次读取
                        lock (_gate) { _busy = false; }
                        request.Completion.TrySetResult(payload); // 只 SetResult，绝不 SetException
                    }
                }
                if (!PumpThreadMessages()) break; // 收到 WM_QUIT：Dispose 中
            }
        }
        catch (Exception)
        {
            // 线程兜底：任何漏网的异常都只让本线程退出（后续读取按超时降级为 null），不杀进程
        }
    }

    /// <summary>消息泵（照抄 UiaCaretProbeThread 模式，wRemoveMsg 用 PM_REMOVE 真正取出消息，
    /// 避免队列里有消息时原地死循环）。收到 WM_QUIT 返回 false 让 Run 退出。</summary>
    private static bool PumpThreadMessages()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PmRemove))
        {
            if (msg.message == WmQuit) return false;
            _ = TranslateMessage(ref msg);
            _ = DispatchMessage(ref msg);
        }
        return true;
    }

    /// <summary>释放读取线程：唤醒消息泵并限时等它退出（不抛、重复调用安全）。</summary>
    public void Dispose()
    {
        Thread? thread;
        ReadRequest? pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            thread = _thread;
            pending = _current;
            _current = null; // 丢弃未处理的请求
        }
        // 丢弃的请求立即按「无内容」收尾：等待方不必再等满超时（之后不再接受新请求）
        pending?.Completion.TrySetResult(null);
        if (thread is null) return; // 从未启动过：无需清理

        var threadId = _threadId; // volatile 读（可能为 0：线程队列尚未就绪，此时靠 _disposed + Join 收尾）
        if (threadId != 0) _ = PostThreadMessage((uint)threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _ = thread.Join(300); // 有上限：OLE 调用卡死时最多等 300ms（IsBackground 不阻塞进程退出）
    }

    /// <summary>退订格式检查（2026-11-10 C1-A）：Windows 剪贴板历史/云剪贴板与密码管理器
    ///（KeePassXC / 1Password / Bitwarden 等）复制密码时会同时放「勿记录/勿上云」标记格式；
    /// 命中即当本轮无内容（<see cref="ReadAsync"/> 与坏 PNG 回退的 <see cref="ReadDibAsync"/> 都覆盖），
    /// 隐私优先、宁可少记一轮。检查自身异常按「不跳过」处理（可用性优先），异常绝不出本方法。
    /// 须在 STA 读取线程上调用（WPF Clipboard 的线程要求）。</summary>
    private static bool IsExcludedByPolicy()
        => ClipboardCapturePolicy.ShouldSkip(Clipboard.ContainsData, Clipboard.GetData);

    // ---- 以下读取实现由 AppServices 原样搬来（含 PngClipboardFormat 常量与各限额/守卫调用） ----

    /// <summary>在读取线程上读取剪贴板载荷（STA 必需）：文本串 / PNG 字节 / Freeze 后的位图 / 文件列表。
    /// 无有效内容或剪贴板被占用返回 null。图像优先取 "PNG" 注册格式：无损保真，且很多截图工具只放它
    ///（无 DIB，ContainsImage 认不出来会被整条丢弃）。</summary>
    private static object? ReadClipboardPayload()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (string.IsNullOrEmpty(text)) return null;
                if (IngestLimits.TextTooLarge(text)) return null; // 超大文本拒收（不截断——截断会破坏回贴保真）
                return text;
            }
            if (TryGetClipboardPng() is { } png) return png;
            if (Clipboard.ContainsImage())
                return ReadDibPayload(); // 忙/空 → 本轮放弃（原语义：有图像格式不落文件分支）
            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList().Cast<string>().ToList();
                // 文件列表通道同样限额（条数/单路径/Σ UTF8 字节）：本通道零限额时 10MB 文本上限
                // 可被「换格式重投」绕过；超限整条拒收
                if (files.Count == 0 || IngestLimits.FileListTooLarge(files)) return null;
                return files;
            }
            return null;
        }
        catch (Exception ex) when (ex is COMException or OutOfMemoryException)
        {
            return null; // 剪贴板忙（其他进程持有）或超大位图内存不足：本轮放弃，下轮变更再取
        }
    }

    private static object? ReadDibPayload()
    {
        try
        {
            if (!Clipboard.ContainsImage()) return null;
            // 解码前防线：按 CF_DIB 头的声明尺寸拒收（GetImage 的 w×h×4 分配先于后台段的
            // 像素限额检查）；预读失败（剪贴板被占/无 DIB/头不全）不拦截，后台段限额仍兜底
            var dib = ClipboardDibGuard.GetDimensions();
            if (dib is { } d && (long)d.Width * d.Height > ImageCodec.MaxImagePixels) return null;
            if (Clipboard.GetImage() is not { } raw) return null;
            try
            {
                raw.Freeze(); // 必须在读取线程上冻结：Freeze 后才能跨线程交给后台段做像素处理
                return raw;
            }
            catch
            {
                return null; // 冻结失败（罕见）：本轮放弃
            }
        }
        catch (Exception ex) when (ex is COMException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>"PNG" 注册格式名（截图工具约定俗成的自定义剪贴板格式）。</summary>
    private const string PngClipboardFormat = "PNG";

    /// <summary>读剪贴板 "PNG" 格式；剪贴板忙或内容不是 PNG 返回 null。</summary>
    private static byte[]? TryGetClipboardPng()
    {
        try
        {
            return Clipboard.ContainsData(PngClipboardFormat)
                ? ImageCodec.PngBytesFromClipboardData(Clipboard.GetData(PngClipboardFormat))
                : null;
        }
        catch (COMException)
        {
            return null; // 剪贴板被占用：本轮放弃，让 DIB 分支自己再试一次
        }
    }

    // ---- 线程/消息泵 P/Invoke（App 工程不用 Core 的 internal NativeMethods，按 UiaCaretProbeThread 惯例本地声明） ----

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

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
