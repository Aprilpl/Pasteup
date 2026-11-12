using System.IO;
using System.Security.Cryptography;

namespace Pasteup.App.Services;

/// <summary>
/// 单实例守卫：互斥体管「谁是正主」，命名事件管「唤起信号」——两个职责分清，
/// 不再让一个事件兼任所有权判定（2026-11-02 安全加固）。名称按数据目录派生：
/// 不同 PASTEUP_DATA = 不同实例域，沙箱冒烟可与实机实例共存，不必先杀光全部实例；
/// 相同数据目录的第二实例照旧只转发信号后退出（--show 语义不变）。
///
/// 抗抢占降级（2026-11-10 复审其他#12 / P3-2）：<c>Local\</c> 命名对象可被**同会话任意进程**
/// 抢先创建、或用受限 DACL 创建/占用（名字可从默认数据目录路径推出）。旧实现此时
/// <c>new Mutex</c>/<c>new EventWaitHandle</c> 直接抛异常，而构造发生在 App.OnStartup 且无兜底，
/// 结果是「启动崩溃」或被 DispatcherUnhandledException 吞掉后的**无托盘/无热键/无窗口僵尸进程**。
/// 现在：拿不到命名对象时**不抛、降级为不启用单实例约束**（本进程按首实例继续运行，可用性优先），
/// 原因经 <see cref="SingleInstanceDegraded"/>/<see cref="DegradedReason"/> 暴露给诊断；唤起信号事件
/// 不可用时 <see cref="SignalShow"/> 静默 no-op、首实例不启动信号线程。前提=同会话任意进程
///（本机 DoS，非越权）——宁可牺牲单实例约束，也不让用户打不开应用。
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>本进程内已持有互斥体的实例 key 集合。Mutex 的所有权是**线程递归**的：同一线程里
    /// 再建一个同 dataDir 的守卫时 <c>WaitOne(0)</c> 会因重入成功而误判成首实例；而一个进程对同一
    /// 数据目录只可能是一个实例，故先用进程内登记表把「本进程已持有」判为第二实例
    ///（跨进程的所有权判定仍走下面的 WaitOne(0)/AbandonedMutexException 路径，不受影响）。</summary>
    private static readonly HashSet<string> InProcessOwned = new(StringComparer.Ordinal);
    private static readonly object InProcessGate = new();

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _showSignal;
    private readonly bool _ownsMutex;   // 本实例是否真正持有互斥体（仅此时 Dispose 才 ReleaseMutex）
    private readonly string? _ownedKey; // 已登记进 InProcessOwned 的 key（仅持有者），Dispose 时移除
    private Thread? _signalThread;      // 仅「首实例且事件可用」时持有信号线程
    private volatile bool _disposed;

    /// <summary>每 tick 由守卫内部线程触发（收到另一实例的唤起信号时）。</summary>
    public event Action? ShowRequested;

    public SingleInstanceGuard(string dataDirectory)
    {
        var key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataDirectory)))[..16];
        Mutex? mutex = null;
        var first = false;
        var ownsMutex = false;
        var degraded = false;
        string? degradedReason = null;

        try
        {
            mutex = new Mutex(initiallyOwned: true, $@"Local\Pasteup_Instance_{key}", out var created);
            if (created)
            {
                first = true;
                ownsMutex = true;
            }
            else if (!IsOwnedInProcess(key))
            {
                // created=false：同名互斥体已存在。持有者已正常释放但对象尚存（还有别的句柄）时
                // WaitOne(0) 立即取得所有权 → 本进程接管；仍被其他进程持有 → false（第二实例）。
                // AbandonedMutexException = 前主进程未释放就退出：异常本身即「已取得所有权」，接管。
                try
                {
                    if (mutex.WaitOne(0))
                    {
                        first = true;
                        ownsMutex = true;
                    }
                }
                catch (AbandonedMutexException)
                {
                    first = true;
                    ownsMutex = true;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException
                                       or IOException)
        {
            // 同会话进程抢先创建同名对象/用受限 DACL 拒访问：降级「不启用单实例」，本进程按首实例继续跑
            //（可用性优先——不崩、不僵；代价是可能与占用者同时运行）。原因只留异常类型名（不含敏感信息）
            degraded = true;
            degradedReason = ex.GetType().Name;
            first = true;
            mutex = null;
        }

        // 唤起信号事件：失败同样不阻塞启动，只是信号不可用（第二实例的 SignalShow 变 no-op）
        EventWaitHandle? signal = null;
        if (mutex is not null)
        {
            try
            {
                signal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\Pasteup_Show_{key}");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException
                                           or IOException)
            {
                degraded = true;
                degradedReason ??= ex.GetType().Name;
                signal = null;
            }
        }

        if (ownsMutex)
        {
            lock (InProcessGate) { _ = InProcessOwned.Add(key); }
            _ownedKey = key;
        }
        _mutex = mutex;
        _showSignal = signal;
        _ownsMutex = ownsMutex;
        IsFirstInstance = first;
        SingleInstanceDegraded = degraded;
        DegradedReason = degradedReason;

        // 只有「首实例且事件可用」才起信号线程；降级/第二实例都不起（降级时没有可等的句柄）
        var signalForThread = signal;
        if (!first || signalForThread is null) return;
        _signalThread = new Thread(() =>
        {
            // _disposed 由 Dispose 侧先置位再 Set 唤醒：醒来看到即退出，迟到的唤起信号丢弃
            while (!_disposed && signalForThread.WaitOne())
            {
                try { ShowRequested?.Invoke(); }
                catch
                {
                    // 回调目标已停机（应用退出中）：信号丢弃，线程随进程结束
                }
            }
        })
        { IsBackground = true };
        _signalThread.Start();
    }

    /// <summary>本进程是否为该数据目录的首个实例。降级模式下恒为 true（不启用单实例约束）。</summary>
    public bool IsFirstInstance { get; }

    /// <summary>命名对象不可用导致单实例约束被降级（正常路径 false）。此时本进程按首实例运行，
    /// 但可能与占用同名对象的进程并存；<see cref="DegradedReason"/> 给出异常类型名供诊断。</summary>
    public bool SingleInstanceDegraded { get; }

    /// <summary>降级原因（命名对象创建/打开抛出的异常类型名）；正常路径为 null。</summary>
    public string? DegradedReason { get; }

    /// <summary>第二实例调用：唤起首实例的面板。事件不可用（降级/被同会话进程占用）时静默 no-op。</summary>
    public void SignalShow()
    {
        if (_showSignal is { } signal) _ = signal.Set();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 只释放自己真正取得的互斥体：第二实例/降级路径不持有，Release 会对非拥有者抛 ApplicationException。
        // _disposed 抢先置位保证幂等。
        if (_ownsMutex)
        {
            if (_ownedKey is not null)
            {
                lock (InProcessGate) { _ = InProcessOwned.Remove(_ownedKey); }
            }
            if (_mutex is not null)
            {
                try { _mutex.ReleaseMutex(); }
                catch (ApplicationException)
                {
                    // 非创建线程调用 Dispose 时释放会失败：句柄关闭后互斥体随线程退出成 abandoned，后来者仍可接管
                }
            }
        }
        _mutex?.Dispose();

        // 先 Set 唤醒阻塞在 WaitOne 的信号线程（看到 _disposed 即退出）、短超时 Join 后再释放句柄——
        // 直接 Dispose 会让等待中的线程抛 ObjectDisposedException，且抛在另一条线程上
        //（App.OnExit 的兜底接不住）把「正常退出 0」变成崩溃退出。
        // 第二实例/降级没有信号线程：不 Set（避免误唤醒首实例的等待线程），只释放自己的句柄
        var signal = _showSignal;
        if (signal is null) return;
        if (_signalThread is not null)
        {
            _ = signal.Set();
            _ = _signalThread.Join(300);
        }
        signal.Dispose();
    }

    private static bool IsOwnedInProcess(string key)
    {
        lock (InProcessGate) { return InProcessOwned.Contains(key); }
    }
}
