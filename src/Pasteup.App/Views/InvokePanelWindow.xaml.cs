using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Pasteup.App.Services;
using Pasteup.App.ViewModels;
using Pasteup.Core.Capture;
using Pasteup.Core.History;
using PstThemeMode = Pasteup.Core.Settings.ThemeMode;

namespace Pasteup.App.Views;

public partial class InvokePanelWindow : Window
{
    private PanelViewModel? _viewModel;

    // 唤起面板前的前台窗口：Enter 粘贴时显式还原焦点（Hide 的自动还原不可靠）
    private IntPtr _pasteTarget;

    // 唤起时该前台窗口的 pid：粘贴注入前的最后复核用固定 pid（hwnd 复用也拦得住，
    // 旧实现现场重读 target 的 pid 会自指通过——复审 P1-2）。跟随 _pasteTarget 一起更新。
    private int _pasteTargetPid;

    // 落位生成号：每次 ShowAtCursor 自增。异步精修的 caret 结果回来时凭它确认自己仍属于
    // 「最近一次唤起」——上一轮迟到的结果不得把新一轮刚落位的面板挪走（2026-11-10 复审问题1）
    private int _placementGeneration;

    // ActivateSelected 重入守卫：改为 async void 后写剪贴板含异步段，连按 Enter/双击不得
    // 重复激活同一条目（旧的同步实现天然串行，不存在这个窗口）
    private bool _activating;

    // 异步抢前台进行中：OnDeactivated 的延迟自藏要跳过这段窗口（详见 OnDeactivated 注释）
    private bool _restoringForeground;

    // 空格预览（Quick Look）：窗口与 VM 复用，生命周期归面板——面板藏/关/粘贴时一并收起
    private PreviewWindow? _preview;
    private PreviewViewModel? _previewVm;
    private PreviewScrimWindow? _scrim;

    public PanelViewModel ViewModel
    {
        get => _viewModel!;
        set
        {
            _viewModel = value;
            value.Items.CollectionChanged += OnItemsChanged;
            // 预览开着时 ↑↓ 换条目 → 预览内容跟随切换（含搜索过滤后的选中重建）
            value.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    public InvokePanelWindow()
    {
        InitializeComponent();
        DataContext = this;
        // 尺寸记忆（DIP）：宽度构造期即还原——ShowAtCursor 首开落位数学用 Width 估值（ActualWidth 尚 0）；
        // 高度在 OnContentRendered 脱离 SizeToContent 时还原；记录见 SizeChanged
        SetCurrentValue(WidthProperty,
            WindowSizeMemory.Restore(App.Current.Settings.PanelWidth, Width, MinWidth));
        Search.TextChanged += (_, _) =>
            SearchHint.Visibility = Search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        Closed += (_, _) => ClosePreview();
        // 菜单内 ↑↓ 端点即停（与列表同惯例，用户否决循环；WPF 菜单默认循环）
        EntryMenu.PreviewKeyDown += OnEntryMenuPreviewKeyDown;
        // 菜单关闭（含命令执行后自动关）把焦点送回选中行：命令触发 RefreshFromEngine 整表重建，
        // 菜单默认把焦点还给已销毁的 PlacementTarget 容器 → 键盘焦点死亡，后续 Esc/Enter/Menu 全落空
        // （2026-11-02 冒烟实锤：删除后按 Menu 键无响应）。粘贴路径面板即藏，此处无害。
        EntryMenu.Closed += (_, _) =>
        {
            _entryMenuKbd = false;
            Dispatcher.BeginInvoke(
                () => { if (IsVisible) FocusList(); },
                System.Windows.Threading.DispatcherPriority.Background);
        };
        // ViewModel 是应用级单例：真关闭（Alt+F4）后必须退订，否则旧窗口整棵视觉树被单例钉活，
        // 每次「关闭 → 再唤起」泄漏一棵（ShowPanel 对已关窗口重建新实例）
        Closed += (_, _) =>
        {
            if (_viewModel is null) return;
            _viewModel.Items.CollectionChanged -= OnItemsChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        };
        // 尺寸上限：宽≤所在屏工作区 50%、高≤100%（替代旧静态 MaxHeight=680；宽旧无上限）。
        // 挂 LocationChanged → 拖动跨屏自动刷新；ShowAtCursor 落位设 Left/Top 时亦触发
        LocationChanged += (_, _) => Services.WindowLimits.ApplyTo(this);
        // 尺寸记忆（DIP）：拖动/边缘缩放后记录，经防抖自动保存落盘（App.ScheduleAutosave）；
        // 还原触发的 SizeChanged 与已存值差 <1，Record 返回 null 静默
        SizeChanged += (_, _) => { if (IsLoaded) RememberPanelSize(); };
        // 注：上滚曾两度尝试「滚轮事件合并 + ScrollToVerticalOffset 大步」削减 VSP 逐档回走
        //（PageUp 实验 0.77s/全程 vs 滚轮 4.17s 证明按调用计费），因注入式压测的滚轮符号在
        // 应用侧系统性反转（新脚本 -120 上滚，与老脚本相反，根因未及归因）无法在本机验证，
        // 滚轮是最高频手势不验证不上线——已撤销，详见任务记录 2026-11-03。
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 面板重建 = 缓存重建时机（全部窗口死亡的间隙里的显示器变化在此自愈）；
        // 在世期间拓扑/缩放变化由钩子实时失效
        Monitors.Invalidate();
        Services.DisplayChangeHook.Attach(this);
        Services.Win32Input.PreferRoundedCorners(this);
        Services.Win32Input.EnableDwmShadow(this);
        // 8 DIP 边缘热区：窗口矩形 == 可见卡片后热区全落在卡面上（取 PinWindow 同款值，
        // 且小于顶栏内容 Margin 12，交互控件零重叠）；非交互区整面可拖（顶栏几乎全被搜索框占据，
        // 若只做把手则实际无处可抓）；拖动/缩放走 WindowDragging 手动引擎，不依赖系统移动循环
        Services.WindowDragging.Attach(this, 8, Services.WindowDragging.IsInteractiveControlSource);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // 交互调整高度前必须脱离 SizeToContent，否则纵向拖动被内容测量高度顶回
        if (SizeToContent != SizeToContent.Manual)
        {
            SizeToContent = SizeToContent.Manual;
            // 放开列表的有限上限（打断首开无限高测量链用的），此后拖大窗口列表随之伸展
            List.MaxHeight = double.PositiveInfinity;
            // 先把交互上限抬到工作区再还原高度：存储高度可大于首开上限 680（XAML 静态值只是
            // 虚拟化守卫），先设 Height 会被 680 压住、之后抬高 Max 也不再回涨
            Services.WindowLimits.ApplyTo(this);
            var memoH = WindowSizeMemory.Restore(App.Current.Settings.PanelHeight, 0, MinHeight);
            SetCurrentValue(HeightProperty,
                memoH > 0 ? memoH : Math.Min(ActualHeight, InitialHeightCap));
        }
        // 首次落位时 ActualWidth 尚为 0，渲染完成后按真实尺寸再刷一次上限（幂等）
        Services.WindowLimits.ApplyTo(this);
        // 首开高度与内容自洽时（Height 设的就是当前值）SizeChanged 不触发，布局完成后显式记一次
        Dispatcher.BeginInvoke(RememberPanelSize, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RememberPanelSize()
    {
        var s = App.Current.Settings;
        if (WindowSizeMemory.Record(ActualWidth, ActualHeight, s.PanelWidth, s.PanelHeight) is { } sz)
        {
            s.PanelWidth = sz.W;
            s.PanelHeight = sz.H;
            App.Current.ScheduleAutosave();
        }
    }

    /// <summary>首开高度上限（旧静态 MaxHeight 值，保持既有观感）；交互上限在 WindowLimits（工作区全高）。</summary>
    private const double InitialHeightCap = 660;

    /// <summary>
    /// 在锚点旁显示：锚点来源由设置决定（Caret = 优先文本插入光标、取不到退回鼠标；Cursor = 始终鼠标），
    /// 规则下 → 上 → 右 → 左 → 贴边（间隙 10 DIP，永不跨屏、不压任务栏）。
    /// 采集链见 CaretAcquisition（native → UIA TextPattern → MSAA，2026-11-10 落地）。
    /// 2026-11-10 复审问题1 重构：UI 线程只做「快速首落位」（native caret / 鼠标）→ Show，
    /// UIA/MSAA 这类跨进程调用 Show 之后由专用 STA 探针线程（UiaCaretProbeThread.ProbeAsync）精修；
    /// !IsActive 的前台还原同样改异步。
    /// 整个方法保持同步返回（窗口必须在本轮调度内 Show，App.xaml.cs 直接调用），异步段都 fire-and-forget。
    /// </summary>
    public void ShowAtCursor()
    {
        // 本轮唤起生成号：精修回调凭它丢弃「上一轮迟到的 caret 结果」，防止旧结果把新面板挪走
        var generation = ++_placementGeneration;
        // 唤起前捕获前台窗口（此时面板尚隐藏，前台必然是用户正在用的应用）：caret 采集与粘贴目标都用它。
        // 同时固化其 pid：粘贴注入前的最后复核按这个 pid 比对（不现场重读 hwnd 的 pid，防 hwnd 复用自指）
        _pasteTarget = Services.Win32Input.ForegroundWindow();
        _pasteTargetPid = (int)Services.Win32Input.ProcessIdOfWindow(_pasteTarget);
        // 快速首落位：只跑本进程廉价调用（native caret / 鼠标），用既有数学一次算好；
        // 跨进程探针没命中时把采集上下文交出来，Show 之后异步精修
        var caretPlaced = PlaceNearAnchorInitial(out var asyncContext);
        // 2026-11-13 根因修复：UIA 的全局焦点元素必须在**面板 Show 之前**快照——Show/Activate 之后
        // 全局焦点已经变成面板自己的 WPF 元素（pid 不匹配），此后才问 GetFocusedElement 只能退回
        // 「窗口里第一个 TextPattern 后代」（Chrome 下永远是地址栏 → 面板每次固定落位）。
        // 快照只等 <=CaptureTimeoutMs（UIA 线程启动即预热，热查询实机 1–3ms），失败/超时即放弃。
        Services.CaretHit? preCaret = null;
        var preCaptureTrace = "";
        if (!caretPlaced && asyncContext is not null)
            preCaret = CaptureFocusedCaretBeforeShow(asyncContext, out preCaptureTrace);
        ViewModel.IsPanelVisible = true; // 先开闸再刷新：本次唤起必见最新数据
        ViewModel.RefreshFromEngine();
        // 唤起回顶（设置可关）：重置必须在 RefreshFromEngine 版本门控之外——引擎未变时
        // refresh 直接跳过、选中恢复不跑。选中首个条目行 + 滚到列表最顶
        var resetTop = App.Current.Settings.SummonResetToTop;
        if (resetTop) ViewModel.ResetToTop();
        Show();
        if (resetTop && ViewModel.Items.Count > 0)
            // 滚到 Items[0]（组头也算列表元素）：首元素顶到视口顶 = 偏移 0；
            // 滚首个条目行会把其上方组头划出视口，视觉上不算「顶部」
            List.ScrollIntoView(ViewModel.Items[0]);
        Activate();
        // 焦点直接落搜索框：唤起即可打字筛选；↑↓ 把焦点移交列表，Ctrl+F / 鼠标点回搜索框
        Search.Focus();
        Search.SelectAll();
        // 无输入路径（单实例 --show 转发、假热键 PostMessage）下 Activate() 会被前台锁拒绝：
        // 面板可见但拿不到键盘焦点（2026-11-02 实机冒烟实锤）。异步抢前台——阶梯里有 8×20ms
        // 与 Alt 点放 4 轮的等待，绝不能再同步睡 UI 线程（同步版最坏 ≈1.04s，面板已 Show 却白块冻住）
        if (!IsActive) _ = TryRestoreForegroundAsync();
        // 首落位没用上 caret：有 Show 前快照就只按真实尺寸复核位置（零跨进程调用）；
        // 没有快照才跑慢的 UIA→MSAA 异步精修（内部已用「窗口内 HasKeyboardFocus 元素」兜底）。
        if (!caretPlaced)
        {
            if (preCaret is { } pre) RefineWithCapturedCaret(pre, generation);
            else if (asyncContext is not null)
                _ = RefineCaretPlacementAsync(asyncContext, preCaptureTrace, generation);
        }
    }

    /// <summary>
    /// Show 前的 UIA 焦点快照（2026-11-13 固定落位根因修复）：只读一次全局焦点元素的 TextPattern caret，
    /// pid 必须命中唤起前的前台进程。成功即用真实 caret 先把窗口摆好（用户看到的第一帧就在锚点旁，
    /// 不再先出现在鼠标处再跳）；校验复用 <see cref="Services.CaretAcquisition.Validate"/> 的三层规则。
    /// 失败/超时/线程忙时不阻塞唤起，trace 交给 Show 后的兜底精修一起写进 placement.log。
    /// </summary>
    private Services.CaretHit? CaptureFocusedCaretBeforeShow(Services.CaretProbeContext context,
        out string trace)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!Services.UiaCaretProbeThread.Shared.TryCaptureFocusedCaret(context, out var raw, out var why))
        {
            trace = $"precapture-fail:{why} ({sw.ElapsedMilliseconds}ms)";
            return null;
        }

        var hit = Services.CaretAcquisition.Validate(context, Services.CaretSource.Uia, raw, why, out var reject);
        if (hit is null)
        {
            // 拒绝也带上探针明细（focused(ct/class) + 原始 rect）：空输入框/假 caret 这类问题
            // 只看 "size 721x35" 无法定位是哪个元素报的（2026-11-14）
            trace = $"precapture-reject:{reject} why:[{why}] ({sw.ElapsedMilliseconds}ms)";
            return null;
        }

        trace = $"precapture:ok:{why} ({sw.ElapsedMilliseconds}ms)";
        var cursor = Services.Win32Input.GetCursorPos();
        var cursorMonitor = Monitors.ContainingPhysicalPoint(cursor.X, cursor.Y) ?? Monitors.Primary();
        if (cursorMonitor is null) return hit;
        Services.Win32Input.TryGetWindowRect(_pasteTarget, out var fgRect);
        ApplyPlacement(cursorMonitor, cursor, fgRect, hit, trace, "precapture");
        return hit;
    }

    /// <summary>
    /// Show 之后用真实窗口尺寸把「Show 前快照」算出的位置再校一次（无跨进程查询，成本≈0）。
    /// 排到 Loaded 优先级：此时布局已完成，ActualWidth/Height 才是最终值。
    /// </summary>
    private void RefineWithCapturedCaret(Services.CaretHit hit, int generation)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // 生成号/可见性守卫：迟到的回调不得把新一轮面板挪走，面板已藏也丢弃
            if (generation != _placementGeneration || !IsVisible) return;
            var caretPoint = hit.Caret;
            if (Monitors.ContainingPhysicalPoint(
                    (caretPoint.Left + caretPoint.Right) / 2, (caretPoint.Top + caretPoint.Bottom) / 2) is null)
                return;
            var cursor = Services.Win32Input.GetCursorPos();
            var cursorMonitor = Monitors.ContainingPhysicalPoint(cursor.X, cursor.Y) ?? Monitors.Primary();
            if (cursorMonitor is null) return;
            Services.Win32Input.TryGetWindowRect(_pasteTarget, out var fgRect);
            ApplyPlacement(cursorMonitor, cursor, fgRect, hit, hit.Detail, "refine");
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 快速首落位（UI 线程，只做本进程廉价调用）：native 系统 caret（GetGUIThreadInfo，微秒级）
    /// → 鼠标指针；取到 caret 就按 caret 数学落位，否则鼠标锚点落位。UIA/MSAA 不在这里跑
    /// ——它们跨进程，UIA 有 150ms 等待、MSAA 可挂起。跨进程查询分两条：
    /// ① Show **之前**只做一次「UIA 全局焦点快照」（<see cref="CaptureFocusedCaretBeforeShow"/>，25ms 上限，
    ///    这是 Chrome/Electron 自绘光标最可靠的来源——Show 后全局焦点就变成面板自己）；
    /// ② 快照失败时，Show 之后才走 <see cref="RefineCaretPlacementAsync"/> 的完整异步链兜底。
    /// 返回 true = 首落位用上了 caret；asyncContext = 需要异步精修时的采集上下文（否则 null）。
    /// </summary>
    private bool PlaceNearAnchorInitial(out Services.CaretProbeContext? asyncContext)
    {
        asyncContext = null;
        var cursor = Services.Win32Input.GetCursorPos();
        var cursorMonitor = Monitors.ContainingPhysicalPoint(cursor.X, cursor.Y) ?? Monitors.Primary();
        if (cursorMonitor is null) return false;

        // 前台窗口矩形：只用于日志与 caret 校验（跨屏/取不到时 Fmt 会标注）
        Services.Win32Input.TryGetWindowRect(_pasteTarget, out var fgRect);

        Services.CaretHit? hit = null;
        var caretTrace = "";
        Services.CaretProbeContext? context = null;
        if (App.Current.Settings.AnchorMode == Pasteup.Core.Settings.PanelAnchorMode.Caret
            && _pasteTarget != IntPtr.Zero)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Win32Input.PhysicalRect? focus = Services.Win32Input.TryGetFocusWindowRect(_pasteTarget,
                out var focusRect)
                ? focusRect
                : null;
            context = new Services.CaretProbeContext(_pasteTarget, focus,
                (x, y) => Monitors.ContainingPhysicalPoint(x, y) is not null);
            // 首落位只跑 native 探针：跨进程的 UIA/MSAA 是「Show 前卡 UI」的根源，挪到异步精修
            hit = Services.CaretAcquisition.TryAcquire(context, Services.CaretAcquisition.NativeOnlyProbes,
                out caretTrace);
            caretTrace += $" ({sw.ElapsedMilliseconds}ms)";
        }

        var usedCaret = ApplyPlacement(cursorMonitor, cursor, fgRect, hit, caretTrace, "initial");
        // 首落位没吃上 caret：把上下文交给 Show 后的异步精修（UIA → MSAA）
        if (!usedCaret && context is not null) asyncContext = context;
        return usedCaret;
    }

    /// <summary>
    /// 用已确定的锚点算落位、写窗口位置并记诊断日志。落位数学与 2026-11-09 定稿完全一致
    /// （下→上→右→左→贴边、10 DIP 间隙、物理像素 SetWindowPos 绕开跨 DPI 的 WPF Left/Top），
    /// 首次落位与 Show 后的异步精修共用；日志字段一个不删，仅末尾加 phase 区分。返回是否用上 caret。
    /// </summary>
    private bool ApplyPlacement(MonitorRect cursorMonitor, PhysicalPoint cursor,
        Win32Input.PhysicalRect fgRect, Services.CaretHit? hit, string caretTrace, string phase)
    {
        var mode = App.Current.Settings.AnchorMode;
        MonitorRect target;
        PanelPlacement.Rect anchor;
        string source;
        var usedCaret = false;
        if (hit is { } h && Monitors.ContainingPhysicalPoint(
                (h.Caret.Left + h.Caret.Right) / 2, (h.Caret.Top + h.Caret.Bottom) / 2) is { } caretMonitor)
        {
            // caret 就是粘贴落点本身：它在哪块屏，面板就在哪块屏
            target = caretMonitor;
            anchor = new PanelPlacement.Rect(h.Caret.Left, h.Caret.Top, h.Caret.Width, h.Caret.Height);
            source = h.Source switch
            {
                Services.CaretSource.Native => "caret/native",
                Services.CaretSource.Uia => "caret/uia",
                Services.CaretSource.Msaa => "caret/msaa",
                _ => "caret",
            };
            usedCaret = true;
        }
        else
        {
            // 自绘光标且 UIA 也拿不到（部分 Electron / 密码框 / UWP）：按用户选择退回鼠标
            target = cursorMonitor;
            anchor = new PanelPlacement.Rect(cursor.X, cursor.Y, 0, 0);
            source = mode == Pasteup.Core.Settings.PanelAnchorMode.Caret ? "mouse" : "mouse/mode";
        }

        var scale = target.Dpi / 96.0;
        // 首次唤起尚未渲染时 ActualWidth/Height 恒 0，用 Width/Height 估值；高度 NaN 时取首开上限。
        // 高度按上限估：上置时算出的底边最靠上，真实更矮只会让间隙变大、不会压到当前行。
        var wDip = ActualWidth > 0 ? ActualWidth : Width;
        var hDip = ActualHeight > 0 ? ActualHeight
            : double.IsNaN(Height) ? InitialHeightCap : Height;
        var (workX, workY, workW, workH) = WorkAreaOf(target);
        var result = PanelPlacement.Solve(
            anchor,
            new PanelPlacement.Rect(workX, workY, workW, workH),
            Math.Round(wDip * scale),
            Math.Round(hDip * scale),
            Math.Round(PanelPlacement.Gap * scale));
        PlacementLog($"summon fg={_pasteTarget} mode={mode} source={source} trace=[{caretTrace}] " +
                     $"cursor=({cursor.X},{cursor.Y}) cursorMon={cursorMonitor.DeviceName} fgRect={Fmt(fgRect)} " +
                     $"anchor=({anchor.X},{anchor.Y},{anchor.W},{anchor.H}) " +
                     $"panel={Math.Round(wDip * scale)}x{Math.Round(hDip * scale)} " +
                     $"work=({workX},{workY},{workW},{workH}) dpi={target.Dpi} rule={result.Rule} " +
                     $"rect=({Math.Round(result.Panel.X)},{Math.Round(result.Panel.Y)}) phase={phase}");
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
        Services.Win32Input.SetWindowPosition(
            hwnd, (int)Math.Round(result.Panel.X), (int)Math.Round(result.Panel.Y));
        return usedCaret;
    }

    /// <summary>
    /// Show 之后的异步精修（Show 前快照失败时才走这里）：补跑 UIA→MSAA 跨进程探针，命中后回 UI 线程
    /// 用真实窗口尺寸重算并 SetWindowPosition。探针在 <see cref="Services.UiaCaretProbeThread"/> 的
    /// 专用 STA 线程上串行执行（UIA 与 MSAA 同一 STA 上下文；整链最多等 200ms，超时即放弃、保持
    /// 鼠标锚点），这里只 await——不占 UI 线程、也不占线程池（复审问题1 收尾）。
    /// 注意此时**全局焦点已是面板自己**，UIA 探针内部走「窗口内 HasKeyboardFocus 元素」兜底
    ///（2026-11-13）；<paramref name="preCaptureTrace"/> 是 Show 前快照的失败原因，拼进日志便于诊断。
    /// 生成号不匹配（用户已再次唤起）或面板已隐藏则丢弃结果（精修只服务本轮唤起）。
    /// 注意：WPF 的 ActualWidth/Height 只能在 UI 线程读，故 await 续回 UI 后才做落位数学。
    /// </summary>
    private async Task RefineCaretPlacementAsync(Services.CaretProbeContext context,
        string preCaptureTrace, int generation)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Services.CaretHit? hit;
        string trace;
        try
        {
            (hit, trace) = await Services.UiaCaretProbeThread.Shared.ProbeAsync(context, 200);
            // 与 initial 行同风格：trace 末尾拼耗时，placement.log 可直接对比首落位/精修成本
            trace += $" ({sw.ElapsedMilliseconds}ms)";
        }
        catch (Exception ex)
        {
            // 精修只是位置校正，失败不得影响面板：记原因后放弃（ProbeAsync 内部已兜底，这里防意外）
            hit = null;
            trace = "exception:" + ex.GetType().Name + $" ({sw.ElapsedMilliseconds}ms)";
        }

        // 保留 Show 前快照的失败原因：一眼能看出是「快照没拿到」才退到 Show 后的兜底链路
        if (preCaptureTrace.Length > 0) trace = preCaptureTrace + "; " + trace;

        // await 已续回 UI 线程：生成号/可见性守卫——迟到的结果不得把新一轮面板挪走，
        // 用户已关闭面板时也丢弃（这类丢弃不记日志，避免噪音）
        if (generation != _placementGeneration || !IsVisible) return;
        if (hit is not { } h)
        {
            // 快照失败 + 兜底链也没命中：补一行 miss（既有实现此处静默，导致「为什么退回鼠标」
            // 在 placement.log 里不可见；2026-11-13 独立复核建议）
            PlacementLog($"summon fg={_pasteTarget} mode={App.Current.Settings.AnchorMode} " +
                         $"source=none trace=[{trace}] phase=refine-miss");
            return;
        }
        // 命中但 caret 中心不在任何显示器上（跨屏拖拽/假坐标瞬态）：不值得为它挪面板，保持首落位
        var caretPoint = h.Caret;
        if (Monitors.ContainingPhysicalPoint(
                (caretPoint.Left + caretPoint.Right) / 2, (caretPoint.Top + caretPoint.Bottom) / 2) is null)
            return;

        // 回 UI 线程后重取光标/前台矩形（仅日志与鼠标兜底用），落位用精修命中的 caret
        var cursor = Services.Win32Input.GetCursorPos();
        var cursorMonitor = Monitors.ContainingPhysicalPoint(cursor.X, cursor.Y) ?? Monitors.Primary();
        if (cursorMonitor is null) return;
        Services.Win32Input.TryGetWindowRect(_pasteTarget, out var fgRect);
        ApplyPlacement(cursorMonitor, cursor, fgRect, h, trace, "refine");
    }

    /// <summary>
    /// !IsActive（无输入路径）时异步抢前台：RestoreForegroundAsync 在后台线程跑阶梯（内部
    /// await Task.Delay 让出），UI 线程保持渲染/响应；完成后回 UI 补焦点（面板可能已被用户
    /// 关闭，IsVisible 守卫）。旧同步版最坏 ≈1.04s，面板已 Show 却白块冻住（复审问题1）。
    /// 抢不到前台是常态（前台锁/UIPI），整体吞掉——面板已可见，最坏只是没拿到键盘焦点。
    /// </summary>
    private async Task TryRestoreForegroundAsync()
    {
        // 先置标志再 await：OnDeactivated 的延迟自藏回调可能在后台还原进行中执行，
        // 此时抢前台尚未完成，不能把面板收掉（旧同步实现里 Dispatcher 回调被同步等待天然挡住）
        _restoringForeground = true;
        try
        {
            // 句柄在 UI 线程上取（Show 后必有）；句柄值跨线程使用无碍
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            await Services.Win32Input.RestoreForegroundAsync(hwnd);
            if (!IsVisible) return;
            Search.Focus();
            Search.SelectAll();
        }
        catch
        {
            // 抢前台失败/窗口已关：忽略——面板已显示，焦点靠用户点击即可
        }
        finally
        {
            _restoringForeground = false;
        }
    }

    private static string Fmt(Services.Win32Input.PhysicalRect r) =>
        r == default ? "<none>" : $"({r.Left},{r.Top},{r.Right},{r.Bottom})";

    /// <summary>placement.log 单文件大小上限：超过即轮转（rename 为 .1 覆盖旧 .1）。
    /// 日志曾无上限且每次唤起同步写盘（复审 P1-2）。</summary>
    private const long PlacementLogRotateBytes = 1L * 1024 * 1024;

    // 异步写日志的串行链：PlacementLog 在唤起/精修热路径上被调用，原先「CreateDirectory +
    // File.AppendAllText」同步落盘占 UI 线程（本机实测 ~0.35ms/次）；改后台串行队列后
    // 调用方零阻塞，且单写者保证行序与轮转互斥。日志丢失/失败不影响任何功能。
    private static readonly object PlacementLogGate = new();
    private static Task _placementLogTail = Task.CompletedTask;

    /// <summary>落位诊断日志（数据目录 logs/placement.log；写不进去时退回 exe 目录，诊断不得影响唤起）。
    /// 数据目录解析与 App.OnStartup 一致（DataDirectoryResolver）：PASTEUP_DATA 非空即沙箱目录
    ///（冒烟隔离用），否则默认 Local（%LOCALAPPDATA%）。
    /// 本次改为「串行后台队列」：UI 线程只拼字符串入队，实际 IO 在链式 Task 里按调用顺序执行。</summary>
    private static void PlacementLog(string line)
    {
        var text = DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine;
        lock (PlacementLogGate)
        {
            // 续在上一次写盘之后：乱序会破坏诊断日志的时间线，轮转也可能与追加互踩
            _placementLogTail = _placementLogTail.ContinueWith(_ => AppendPlacementLog(text),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>后台线程写一行日志；失败静默（诊断不得影响功能）。</summary>
    private static void AppendPlacementLog(string text)
    {
        try
        {
            var dir = System.IO.Path.Combine(DataDirectory(), "logs");
            System.IO.Directory.CreateDirectory(dir);
            AppendRotatingLog(System.IO.Path.Combine(dir, "placement.log"), text);
            return;
        }
        catch
        {
            // 落到下面的兜底
        }
        try
        {
            AppendRotatingLog(System.IO.Path.Combine(AppContext.BaseDirectory, "placement.log"), text);
        }
        catch
        {
            // 诊断失败忽略
        }
    }

    /// <summary>
    /// 追加一行并按大小轮转（&gt;1MB 时把现档改名 .1、覆盖旧 .1，再写新行）。
    /// 轮转用 File.Move(overwrite:true)（Win32 ReplaceFile/MoveFileEx 语义，原子替换）：
    /// 崩溃最多丢「刚刚这一行」，不会留下写了一半的 .1 或主档。
    /// </summary>
    private static void AppendRotatingLog(string path, string text)
    {
        try
        {
            if (System.IO.File.Exists(path) && new System.IO.FileInfo(path).Length > PlacementLogRotateBytes)
                System.IO.File.Move(path, path + ".1", overwrite: true);
        }
        catch
        {
            // 轮转失败（文件被占用/权限）：继续追加；宁可单文件继续变大也不丢诊断
        }
        System.IO.File.AppendAllText(path, text);
    }

    private static string DataDirectory()
    {
        // 与 App.OnStartup 同源（DataDirectoryResolver）：PASTEUP_DATA 非空即沙箱目录（冒烟隔离用），
        // 否则默认 Local（%LOCALAPPDATA%），不随域漫游/云同步
        return DataDirectoryResolver.Resolve();
    }

    /// <summary>工作区（枚举失败回退整屏矩形，同 WindowLimits.Caps 约定）。</summary>
    private static (int X, int Y, int W, int H) WorkAreaOf(MonitorRect m) =>
        m.WorkWidth > 0
            ? (m.WorkX, m.WorkY, m.WorkWidth, m.WorkHeight)
            : (m.X, m.Y, m.Width, m.Height);

    public void HidePanel()
    {
        ClearRowLift(); // 悬停阴影随面板一起拆（隐藏不触发 MouseLeave，见行悬停注释）
        ClosePreview();
        ViewModel.IsPanelVisible = false; // 隐藏期引擎事件不再整表重建（版本门控兜下次唤起补刷）
        Hide();
    }

    /// <summary>把键盘焦点移到列表选中项（↑↓ 从搜索框移交焦点用）。
    /// 虚拟化下选中项容器可能尚未生成，ScrollIntoView 后延一拍再取。</summary>
    private void FocusList()
    {
        if (List.SelectedItem is not null) List.ScrollIntoView(List.SelectedItem);
        Dispatcher.BeginInvoke(() =>
        {
            if (List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) is ListBoxItem lbi)
                lbi.Focus();
            else if (List.Items.Count > 0 && List.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem first)
                first.Focus();
            else
                List.Focus();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.Escape:
                // 分层退避：预览开着先收预览，再按才藏面板
                if (_preview is { IsVisible: true }) ClosePreview();
                else HidePanel();
                e.Handled = true;
                return;
            case Key.Enter:
                ActivateSelected();
                e.Handled = true;
                return;
            case Key.Up when !ctrl && !shift:
            case Key.Down when !ctrl && !shift:
                // 窗口层统一接管 ↑↓（含焦点在搜索框时）：移动选中并把焦点移交列表；
                // 边界停住不循环（MoveSelection 钳制），此后焦点在列表仍走本分支，行为一致
                ViewModel.MoveSelection(e.Key == Key.Up ? -1 : 1);
                FocusList();
                e.Handled = true;
                return;
            case Key.Space:
                // 搜索框里空格就是空格；焦点在列表时 = 预览开/收（Quick Look）
                if (!Search.IsKeyboardFocusWithin)
                {
                    TogglePreview();
                    e.Handled = true;
                }
                return;
            case Key.P when ctrl:
                ViewModel.TogglePinCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.S when ctrl:
                // 收藏切换（Ctrl+F 已被搜索焦点占用）；鼠标入口 = 右键菜单「收藏」，此处为键盘等效
                ViewModel.ToggleFavoriteCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.Apps:
            case Key.F10:
            case Key.System when e.SystemKey == Key.F10:
                // Menu / F10 / Shift+F10 = 右键等效：在选中项上开菜单（键盘可达入口）。
                // F10 不分 Shift：无菜单栏的窗口里裸 F10 无默认行为，与 Shift+F10 同效最直白
                OpenEntryMenuAtSelection();
                e.Handled = true;
                return;
            case Key.Delete when !Search.IsKeyboardFocusWithin:
                // 焦点在搜索框时 Delete 是文本编辑，不能触发「删除选中历史」（否则搜索框里按
                // Delete 会误删条目——2026-11-02 冒烟实锤：搜索词没变、历史少了一条）
                ViewModel.RemoveSelectedCommand.Execute(null);
                e.Handled = true;
                return;
            case Key.F when ctrl:
                Search.Focus();
                Search.SelectAll();
                e.Handled = true;
                return;
            case Key.D1 when ctrl:
            case Key.D2 when ctrl:
            case Key.D3 when ctrl:
            case Key.D4 when ctrl:
            case Key.D5 when ctrl:
            case Key.D6 when ctrl:
            case Key.D7 when ctrl:
            case Key.D8 when ctrl:
                var tab = (PanelTab)(e.Key - Key.D1);
                ViewModel.SelectTabCommand.Execute(tab);
                e.Handled = true;
                return;
        }
        // 未处理的可打印字符（含 IME 处理键）：转入搜索框（面板即搜）。
        // IME 激活时字母以 ImeProcessed 到达，不转焦点会让 IME 悬空组合（候选窗致面板失焦自藏，
        // 2026-11-02 冒烟实锤）；首字符还会经 TextChanged→Refresh 重建容器、ListBox 抢回焦点，
        // 故延一拍压回搜索框，保住「焦点常驻搜索框」
        if (!ctrl && e.Key is >= Key.A and <= Key.Z or >= Key.D0 and <= Key.D9 or Key.ImeProcessed)
        {
            Search.Focus();
            Dispatcher.BeginInvoke(() => { if (!Search.IsKeyboardFocusWithin) Search.Focus(); },
                System.Windows.Threading.DispatcherPriority.Loaded);
            // 让搜索框自己收到该字符
        }
    }

    /// <summary>
    /// 取用选中条目（Enter / 双击 / 菜单粘贴共用）：先异步写回剪贴板（图像解码等重活在后台），
    /// 成功后再 Touch/Hide，最后在后台还原前台并注入 Ctrl+V。async void + 重入守卫：写剪贴板
    /// 含异步段，连按 Enter/双击不得重复激活（旧同步实现天然串行，不存在这个窗口）。
    /// </summary>
    private async void ActivateSelected()
    {
        if (_activating) return;
        if (ViewModel.ActiveEntry is not { } entry) return;
        _activating = true;
        try
        {
            var app = App.Current;
            // 契约：WriteEntryAsync 内部吞异常。必须先写完剪贴板再 Hide——注入的 Ctrl+V
            // 读的是系统剪贴板，写回没完成就粘贴会粘到旧内容
            await app.IngestService.WriteEntryAsync(entry);
            // 写回含异步段（图像解码在后台）：期间用户可能已 Alt+F4 真关闭面板。已关窗口不能再
            // Hide（抛 InvalidOperationException），且用户意图已是取消——直接放弃本次取用
            if (!IsLoaded) return;
            // 取用即 MRU：刷新时间戳并跳到置顶区后第一位（PasteOnEnter 关闭时同样生效）。
            // 先关闸再 Touch：Touch 的 Changed 在「可见」状态下会触发一次整表重建，紧接着 HidePanel
            // 把这次重建白做掉（复审 P1-2）；关闸后隐藏期刷新走版本门控，下次唤起自然补刷。
            //（三条入口 Enter/双击/菜单粘贴共用本方法，都会 HidePanel，语义一致。）
            ViewModel.IsPanelVisible = false;
            app.History.Touch(entry.Id);
            ClosePreview();
            HidePanel();
            if (app.Settings.PasteOnEnter)
            {
                // 目标句柄与 pid 在 UI 线程成对捕获：后台还原链可能耗 1s，期间再次唤起会改写字段，
                // 本次粘贴仍必须发往「按下 Enter 时的那个窗口」，且复核用固定 pid（防 hwnd 复用）
                var pasteTarget = _pasteTarget;
                var pasteTargetPid = _pasteTargetPid;
                _ = Task.Run(async () =>
                {
                    // 显式还原唤起前的前台窗口；还原失败即放弃注入——剪贴板内容已就位，
                    // 用户可手动 Ctrl+V，盲发只会把内容打进错误的前台窗口（含提权目标：UIPI 下按键本就会被丢弃）
                    var (restored, altTapUsed) = await Services.Win32Input.RestoreForegroundAsync(pasteTarget);
                    if (!restored) return;
                    if (altTapUsed)
                    {
                        // Alt 点放可能把目标应用拉进菜单栏模式：先 Esc 关掉再粘贴，否则 Ctrl+V 被菜单循环吞掉
                        Services.Win32Input.SendEsc();
                    }
                    Core.Clipboard.PasteSimulator.SendPaste(stillSafe: () =>
                        Services.Win32Input.ForegroundBelongsTo(pasteTarget, pasteTargetPid));
                });
            }
        }
        finally
        {
            _activating = false; // 只守激活路径（Hide 已同步完成），后台注入不再占用守卫
        }
    }

    // ===== 空格预览（Quick Look） =====

    private void OnItemsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => UpdateEmptyHint();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PanelViewModel.SelectedTab))
        {
            UpdateEmptyHint();
            return;
        }
        if (e.PropertyName != nameof(PanelViewModel.SelectedItem)) return;
        if (_preview is not { IsVisible: true }) return;
        if (ViewModel.ActiveEntry is { } entry) _preview.ApplyEntry(entry, PanelMonitor());
        else ClosePreview(); // 选中落到组头/空态等无条目项
    }

    /// <summary>空态文案随标签切换：收藏空态给右键菜单/Ctrl+S 引导（发现性兜底），其余用通用文案。</summary>
    private void UpdateEmptyHint()
    {
        EmptyHint.Visibility = ViewModel.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = ViewModel.SelectedTab == PanelTab.Favorite
            ? "还没有收藏 — 右键条目选「收藏」，或按 Ctrl+S 收藏常用的片段"
            : "暂无剪贴板内容 — 复制点什么试试";
    }

    private void TogglePreview()
    {
        if (_preview is { IsVisible: true })
        {
            ClosePreview();
            return;
        }
        if (ViewModel.ActiveEntry is not { } entry) return;
        _previewVm ??= new PreviewViewModel();
        if (_preview is null || !_preview.IsLoaded)
        {
            _preview = new PreviewWindow { ViewModel = _previewVm };
            _preview.CloseRequested += ClosePreview;
        }
        _preview.ApplyEntry(entry, PanelMonitor());
        // ShowActivated=False + WS_EX_NOACTIVATE：不夺激活，面板保持前台与键盘焦点
        _preview.Show();
        ShowScrim();
    }

    // ===== 预览遮罩（剧院式全压：面板所在屏整屏压暗，预览唯一豁免） =====

    private void ShowScrim()
    {
        if (_preview is null) return;
        if (_scrim is null || !_scrim.IsLoaded)
        {
            _scrim = new PreviewScrimWindow();
            _scrim.DismissRequested += ClosePreview; // 点暗处 = Space/Esc 同效收起
        }
        // 中心点/主屏都枚举不到（无显示器环境）时无从盖屏：跳过遮罩，预览窗照常显示
        var monitor = PanelMonitor() ?? Monitors.Primary();
        if (monitor is not null) _scrim.CoverMonitor(monitor);
        _scrim.ShowScrim();
        // Topmost 带内新 Show 会窜到最顶：把遮罩钉回「面板之上、预览之下」
        Services.Win32Input.PlaceBelow(_scrim, _preview);
    }

    private void ClosePreview()
    {
        if (_preview is { IsVisible: true }) _preview.Hide();
        _scrim?.HideScrim();
    }

    /// <summary>
    /// 空闲深度清理（App 空闲 tick 调，此面板必已隐藏）：预览/遮罩窗真关闭而非 Hide
    /// （D3D 面/可视树随 Close 释放，Hide 会驻留 ~70MB 提交），预览 VM 连同其全尺寸
    /// 解码图一并弃。两窗均无 Closing 拦截；TogglePreview/ShowScrim 按 IsLoaded/null
    /// 自动重建，下次 Space 付出 ~20-50ms 重建成本。
    /// </summary>
    public void ReleasePreviewResources()
    {
        _preview?.Close();
        _preview = null;
        _scrim?.Close();
        _scrim = null;
        _previewVm = null;
    }

    /// <summary>面板中心点所在显示器（面板可被拖到任意屏；预览随屏落位）。</summary>
    private MonitorRect? PanelMonitor() => Services.WindowLimits.MonitorOfWindowCenter(this);

    // ===== 行悬停浮起（row-lift 定稿 B 下坠柔影×极淡底，docs/prototypes/row-lift/） =====
    // 阴影不进触发器/资源：Effect 经 DynamicResource 挂上后主题字典整替换不重解析（画刷会换、
    // Effect 这个 Freezable 不会），且资源字典里 Freezable 内部的 DynamicResource（如效果颜色）
    // 压根不解析、回落到依赖属性默认值——2026-11-05 实测浅色主题残留 100% 黑影，只能在代码后台
    // 按令牌现查现建。**渲染端忽略 Color 的 alpha 分量**（实测 #1C0F282C 渲染成不透明
    // RGB(15,40,44)，浅色主题阴影 5 倍过重）：令牌 alpha 拆到 Opacity、颜色转不透明再赋值。
    // 2026-11-06 审计④：每 MouseEnter 现建 Effect，滚动时行从光标下连发扫过即连发建/拆位图
    // 合成面（实测 +15% 滚动 CPU）——改按令牌色缓存一个冻结实例共享挂载（Freezable 冻结后可被
    // 多元素复用引用，悬起只换引用不建渲染资源），令牌色随换主题变化时下次悬起自然重建；
    // 淡底仍走模板触发器（画刷无重解析问题），选中行淡底被 accent 底豁免、阴影照常。
    // 阴影残留清理三路：MouseLeave 拆除；换行悬起时拆旧行；HidePanel 拆当前行（窗口隐藏不触发
    // MouseLeave，且版本门控跳过重建时视觉树原样复用，不拆的话下次唤起阴影贴在未悬停的行上）。
    // VSP 回收复用的瞬时残留由 WPF 布局同步的 over 补 Leave 自愈，无需专门钩子。
    private System.Windows.Media.Effects.DropShadowEffect? _rowLiftShadow;
    private System.Windows.Media.Color _rowLiftShadowToken;
    private Border? _rowLiftRow;

    private void OnRowMouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is not Border row) return;
        if (TryFindResource("RowLiftShadowColor") is not System.Windows.Media.Color c) return;
        if (_rowLiftRow is not null && !ReferenceEquals(_rowLiftRow, row))
            _rowLiftRow.Effect = null; // 上一行还挂着（隐藏/回收路径没走 Leave）：先拆再挂新行
        if (_rowLiftShadow is null || _rowLiftShadowToken != c)
        {
            var shadow = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 16,
                Direction = 270,   // 正下方（默认 315 是右下）
                ShadowDepth = 4,
                Color = System.Windows.Media.Color.FromRgb(c.R, c.G, c.B),
                Opacity = c.A / 255.0,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            };
            shadow.Freeze();
            _rowLiftShadow = shadow;
            _rowLiftShadowToken = c;
        }
        _rowLiftRow = row;
        row.Effect = _rowLiftShadow;
    }

    private void OnRowMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is not Border row) return;
        row.Effect = null;
        if (ReferenceEquals(_rowLiftRow, row)) _rowLiftRow = null;
    }

    /// <summary>拆当前悬停行阴影（HidePanel 调；见上方注释「阴影残留清理三路」）。</summary>
    private void ClearRowLift()
    {
        if (_rowLiftRow is { } r) r.Effect = null;
        _rowLiftRow = null;
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && Extensions.FindParent<ListBoxItem>(d) is not null)
            ActivateSelected();
    }

    // ===== 复制项右键菜单（原型 docs/prototypes/context-menu/：右键即选中 → 光标处浮出） =====

    // 菜单当前是否由键盘路径（Menu/F10）打开：是则 ContextMenuOpening 不重置定位
    // （原生 VK_APPS→WM_CONTEXTMENU 与程序化开同一菜单实例，重置会把「选中项下方」覆盖成光标处）
    private bool _entryMenuKbd;

    /// <summary>右键即选中被击条目再开菜单；组头/列表空白处不开（ContextMenuOpening 先于显示）。</summary>
    private void OnEntryMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = e.OriginalSource is DependencyObject d
            && Extensions.FindParent<ListBoxItem>(d) is { } lbi
                ? List.ItemContainerGenerator.ItemFromContainer(lbi)
                : null;
        if (item is not RowVm) // 行/瓦片同构于 RowVm（图像 Tab 瓦片同样可选中/预览/激活）
        {
            e.Handled = true; // 组头 IsHitTestVisible=False 会落到列表背景，与空白一并拦下
            return;
        }
        // 菜单命令作用于被右击的项；Esc 关菜单后键盘流从这项原地继续（选中已迁移）
        ViewModel.SelectedItem = item;
        UpdateEntryMenuState();
        // 右键路径：跟随鼠标；键盘路径会改 PlacementTarget，此处复位
        if (!_entryMenuKbd)
        {
            EntryMenu.PlacementTarget = null;
            EntryMenu.Placement = PlacementMode.MousePoint;
        }
    }

    /// <summary>置顶/收藏的文案与图标随条目状态反转（pin↔pin-off U+F602/U+F604、star↔star-off U+F710/U+F71B）。</summary>
    private void UpdateEntryMenuState()
    {
        if (ViewModel.ActiveEntry is not { } entry) return;
        MenuPinItem.Header = entry.Pinned ? "取消置顶" : "置顶";
        MenuPinIcon.Text = entry.Pinned ? "\uF604" : "\uF602";
        MenuFavoriteItem.Header = entry.Favorite ? "取消收藏" : "收藏";
        MenuFavoriteIcon.Text = entry.Favorite ? "\uF71B" : "\uF710";
    }

    /// <summary>Menu / Shift+F10：在选中项下方开菜单（虚拟化下容器可能未生成，滚动后延一拍）。</summary>
    private void OpenEntryMenuAtSelection()
    {
        if (ViewModel.ActiveEntry is null) return;
        var item = ViewModel.SelectedItem;
        List.ScrollIntoView(item);
        // 时间上错开 Apps 键的 keyUP（注入/人手 ≈30–100ms）：keyUP 落在刚开的菜单上会被
        // MenuBase 当「再按关闭」立即吃掉；优先级推迟无效（dispatcher 在两键事件空档即执行）
        var openTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        openTimer.Tick += (_, _) =>
        {
            openTimer.Stop();
            if (List.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem lbi)
            {
                UpdateEntryMenuState();
                _entryMenuKbd = true;
                EntryMenu.PlacementTarget = lbi;
                EntryMenu.Placement = PlacementMode.Bottom;
                EntryMenu.IsOpen = true;
            }
        };
        openTimer.Start();
    }

    private void OnEntryMenuPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down)) return;
        var menus = EntryMenu.Items.OfType<MenuItem>().ToList();
        if (menus.Count == 0) return;
        // 首项再↑ / 末项再↓：吞掉，不循环回绕
        if ((e.Key == Key.Up && menus[0].IsHighlighted) ||
            (e.Key == Key.Down && menus[^1].IsHighlighted))
            e.Handled = true;
    }

    private void OnMenuPaste(object sender, RoutedEventArgs e) => ActivateSelected();

    private void OnMenuCopy(object sender, RoutedEventArgs e)
    {
        // 复制 = 仅写剪贴板：不 Touch（MRU 不动）、面板保持打开、不注入 —— 与 Enter 的语义分界。
        // WriteEntryAsync 契约内部吞异常；图像解码在后台段，不再冻 UI 线程
        if (ViewModel.ActiveEntry is { } entry) _ = App.Current.IngestService.WriteEntryAsync(entry);
    }

    private void OnMenuFavorite(object sender, RoutedEventArgs e)
        => ViewModel.ToggleFavoriteCommand.Execute(null);

    private void OnMenuPin(object sender, RoutedEventArgs e)
        => ViewModel.TogglePinCommand.Execute(null);

    private void OnMenuDelete(object sender, RoutedEventArgs e)
        => ViewModel.RemoveSelectedCommand.Execute(null);

    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (IsActive) return;
        // 异步抢前台（TryRestoreForegroundAsync）进行中：此刻「前台还不是面板」正是预期中间态，
        // 延迟自藏必须跳过——旧同步实现里 Dispatcher 回调被同步等待挡住，等到抢完才判断，
        // 换成 async 后回调会插进等待间隙，否则会把即将被激活的面板提前藏掉
        if (_restoringForeground) return;
        // Deactivated 即刻隐藏会吞掉双击的第二下；延迟一拍再确认失焦
        Dispatcher.BeginInvoke(() => { if (!IsActive && !_restoringForeground) HidePanel(); },
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnToggleTheme(object sender, RoutedEventArgs e)
    {
        var app = App.Current;
        var next = app.Settings.Theme == PstThemeMode.Dark ? PstThemeMode.Light : PstThemeMode.Dark;
        app.Settings.Theme = next;
        Theme.ThemeManager.Apply(next);
        app.ScheduleAutosave(); // 主题切换同样走防抖后台保存（同步全量落盘在千条历史时 100-300ms）
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e) => App.Current.ShowSettings();
}

file static class Extensions
{
    public static T? FindParent<T>(DependencyObject? from) where T : DependencyObject
    {
        var cur = from;
        while (cur is not null)
        {
            if (cur is T t) return t;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur);
        }
        return null;
    }
}
