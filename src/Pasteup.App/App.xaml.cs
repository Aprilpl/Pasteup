using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Pasteup.App.Poc;
using Pasteup.App.Services;
using Pasteup.App.Theme;
using Pasteup.App.ViewModels;
using Pasteup.App.Views;
using Pasteup.Core.Clipboard;
using Pasteup.Core.History;
using Pasteup.Core.Hotkeys;
using Pasteup.Core.Settings;

namespace Pasteup.App;

public partial class App : Application
{
    internal static readonly long StartupTicks = Environment.TickCount64;

    private ServiceProvider? _services;
    private Services.SingleInstanceGuard? _instanceGuard;
    private TaskbarIcon? _tray;
    private MenuItem? _pinListMenu;
    private InvokePanelWindow? _panel;
    private SettingsWindow? _settingsWindow;
    private readonly DispatcherTimer _autosaveTimer;
    // 空闲清理：所有窗口隐藏（纯托盘驻留）后第一个 tick 深度清理（关预览/设置窗+清缓存）
    // 并修剪工作集；窗口再显重新武装
    private readonly DispatcherTimer _idleTrimTimer;
    private readonly Services.IdleTrimState _idleTrim = new();
    // 保存串行化 gate：后台自动保存与退出路径的同步保存共用，避免两次写同一路径 .tmp 互踩
    private readonly object _saveGate = new();

    internal HistoryEngine History => _services!.GetRequiredService<HistoryEngine>();
    internal HistoryStore Store => _services!.GetRequiredService<HistoryStore>();
    internal AppSettings Settings { get; private set; } = new();
    internal SettingsStore SettingsStore => _services!.GetRequiredService<SettingsStore>();
    internal HotkeyService Hotkeys => _services!.GetRequiredService<HotkeyService>();
    internal ClipboardMonitor ClipboardMonitor => _services!.GetRequiredService<ClipboardMonitor>();
    internal ClipboardIngestService IngestService => _services!.GetRequiredService<ClipboardIngestService>();
    internal Services.SourceAppIconService SourceIcons => _services!.GetRequiredService<Services.SourceAppIconService>();
    internal PanelViewModel PanelVm => _services!.GetRequiredService<PanelViewModel>();
    internal PinCoordinator Pins { get; } = new();

    /// <summary>退出中标志：置真后窗口的关闭拦截（隐藏进托盘）放行真关闭，避免 Shutdown 被卡住。</summary>
    internal bool IsExiting { get; private set; }

    private bool _trayHintShown;

    public App()
    {
        _autosaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        // 后台保存：千条历史的序列化+落盘挪出 UI 线程（TrySaveAll 内部 gate 防并发写互踩）。
        // 增量保存：一般只向 journal 追加一行，删除/清空/首次保存时内部自动全量重写
        _autosaveTimer.Tick += (_, _) => { _autosaveTimer.Stop(); _ = Task.Run(() => TrySaveAll()); };

        _idleTrimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _idleTrimTimer.Tick += (_, _) =>
        {
            if (!_idleTrim.OnTick(Services.WorkingSetTrimmer.AnyVisibleOwnWindow())) return;
            // 空闲深度清理（边沿触发：全窗口隐藏后的首个 tick）：先把「无用的内容」关掉——
            // 预览/遮罩窗真关闭（Hide 会驻留 ~70MB D3D 提交）、隐藏的设置窗真关闭、缩略图
            // 缓存弃置；再 GC 收编刚释放的原生位图，最后修剪工作集。会话内（有窗口可见）不触发
            _panel?.ReleasePreviewResources();
            if (_settingsWindow is { IsVisible: false }) _settingsWindow.Close();
            ViewModels.ThumbConverter.ClearCaches();
            Services.WorkingSetTrimmer.Trim();
        };
    }

    public static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // POC / 辅助模式：不进入常驻流程；各模式自行决定退出时机（overlay/coldstart 为异步测量）
        var args = e.Args;
        if (args.Length > 0 && PocHarness.Handles(args))
        {
            PocHarness.Run(args);
            return;
        }

        // 信任边界：PASTEUP_DATA 由父进程环境决定，指向哪历史/截图/日志就落哪（含网络路径）——
        // 沙箱测试用；正常启动只有用户自己设置它。空串/空白视同未设置：?? 不滤空串，
        // Path.Combine("",…) 会把全部数据落成相对 CWD 的路径，与按数据目录派生的单实例键自相矛盾。
        // 默认目录从 Roaming（%APPDATA%）改到 Local（%LOCALAPPDATA%）（2026-11-10 复审 C1 落地·B）：
        // Roaming 会被域漫游/OneDrive 等同步到其他设备与云端，而这里落的是**明文剪贴板内容**
        //（history.json/journal、cache PNG、logs）——迁到 Local 才保证不随漫游离开本机；
        // DPAPI 加密维持暂缓（C1 的另一半，未做前仍只假设本机账户边界）。
        // ResolveWithMigration 负责一次性迁移且**不抛、不阻塞启动**：优先 Directory.Move；
        // Move 失败（旧实例仍运行占用文件/权限/跨卷重定向）降级递归复制并保留旧目录
        //（note=copied-old-kept：数据已在新目录，提示用户重启后旧目录可清理）；迁移彻底失败时
        // **本次沿用旧 Roaming 目录**（用户不会突然变成空历史；旧实例运行中还能被单实例守卫识别），
        // 新目录半成品已清理，下次启动自动重试（CrashLog 记 data-migration 便于诊断）。
        var (dataDir, migrationNote) = DataDirectoryResolver.ResolveWithMigration();
        CrashLog.Init(dataDir);
        if (migrationNote is not null)
        {
            CrashLog.Write("data-migration", new InvalidOperationException(migrationNote),
                "数据目录从 Roaming 迁移到 Local（明文历史不再随漫游/云同步）");
        }

        // 数据目录不可写=核心功能整体失效（历史/设置读写全灭、CrashLog 主路径全灭），曾以
        // 「空历史+零日志」形态静默运行整个会话无人察觉（2026-11-12 低完整性标签事故：exe 被
        // 打上 Low 标签→进程低完整性→AppData 全部不可写）。必须像热键/剪贴板监听失败一样可见化：
        // 弹窗告知 + CrashLog（经 exe 目录兜底）留现场；弹窗后继续运行（可用性优先——面板/截图
        // 等不依赖数据目录的功能仍可用）。
        if (!DataDirectoryResolver.IsWritable(dataDir, out var dataDirFailure))
        {
            CrashLog.Write("data-dir-unwritable", dataDirFailure!, $"数据目录不可写：{dataDir}");
            MessageBox.Show(
                $"数据目录不可写，历史记录与设置将无法保存：\n{dataDir}\n\n" +
                $"{dataDirFailure!.GetType().Name}: {dataDirFailure.Message}\n\n" +
                "常见情形：目录权限被改、被其他程序占用，或 exe 位于低完整性标签的目录\n" +
                "（沙箱/安全软件残留，可用 icacls <目录> /setintegritylevel medium 恢复）。",
                "Pasteup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // 三条线程路径的全局异常钩子都要挂，且先于任何窗口/托盘/监听初始化：
        // - DispatcherUnhandledException：UI 线程，弹窗后继续运行
        // - AppDomain.UnhandledException：消息窗口线程/Task 等后台线程，进程已无法挽救，落盘后带栈退出
        // - UnobservedTaskException：Task.Run 内未观察异常，记录并吞掉（默认策略本就不崩）
        DispatcherUnhandledException += (_, ex) =>
        {
            CrashLog.Write("dispatcher", ex.Exception, "UI 线程未处理异常（已恢复，应用继续运行）");
            MessageBox.Show(ex.Exception.Message, "Pasteup", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            if (ex.ExceptionObject is Exception exc)
                CrashLog.Write("unhandled", exc, "后台线程未处理异常，进程即将终止");
        };
        TaskScheduler.UnobservedTaskException += (_, ex) =>
        {
            CrashLog.Write("unobserved-task", ex.Exception, "Task 未观察异常（已吞掉）");
            ex.SetObserved();
        };

        var guard = new Services.SingleInstanceGuard(dataDir);
        // 消息层（热键/剪贴板监听窗口）兜底吞掉的异常转 CrashLog：Core 不落盘日志，经静态事件上报。
        // 该订阅在消息窗口线程上执行：只做 CrashLog 文件 IO、不碰 UI，不构成「消息线程等 UI」的环。
        Core.Interop.MessageWindow.UnhandledWndProcException +=
            ex => CrashLog.Write("wndproc", ex, "消息窗口 WndProc 异常（已吞掉，消息丢弃）");
        if (!guard.IsFirstInstance)
        {
            // 已有实例：唤起它的面板后退出。注意此处 _instanceGuard 仍为 null（只有首实例才赋值），
            // 必须用局部变量发信号——2026-11-02 冒烟实锤：这里 NRE 会被异常钩子接住弹模态框，第二副本卡死不退
            guard.SignalShow();
            Shutdown(0);
            return;
        }
        _instanceGuard = guard;
        guard.ShowRequested += () =>
        {
            try { Dispatcher.BeginInvoke(ShowPanel); }
            catch
            {
                // Dispatcher 已停机（应用退出中）：信号丢弃
            }
        };

        var services = new ServiceCollection();
        services.AddSingleton(new HistoryStore(dataDir));
        services.AddSingleton<HistoryEngine>();
        services.AddSingleton<ClipboardMonitor>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<SettingsStore>(_ => new SettingsStore(dataDir));
        services.AddSingleton<PanelViewModel>();
        services.AddSingleton<ClipboardIngestService>();
        services.AddSingleton<Services.SourceAppIconService>();
        _services = services.BuildServiceProvider();

        Settings = SettingsStore.Load();
        // 开机自启对齐（2026-11-14）：以设置项为准——开启时自愈 exe 路径（便携版换目录/升级后旧值失效），
        // 关闭时清掉本程序形态的遗留 Run 项（同名但指向其他程序的手工值保留）。best-effort：注册表被
        // 组策略/权限锁死时不拦启动（只是不能自启），失败记 CrashLog 供排查；设置页开关另有可见化弹窗。
        try { StartupRegistration.Default.Reconcile(Settings.StartWithWindows, StartupRegistration.CurrentExePath); }
        catch (Exception ex) { CrashLog.Write("startup-registration", ex, "开机自启注册表对齐失败（忽略）"); }
        History.Capacity = Settings.HistoryCapacity;
        History.RetentionDays = Settings.RetentionDays;
        History.Load(Store.Load());
        History.Changed += ScheduleAutosave;
        // 启动期图像缓存配额（复审问题5 第2批）：先清孤儿主图/缩略图，再按最旧「非置顶/收藏」
        // 淘汰超预算图像条目并立即删文件；engine.Remove 经上方 Changed 触发防抖自动保存。
        // 同步在 UI 线程执行（启动期一次，量级同原孤儿清理）；置顶/收藏图像不删（可能仍超配额）。
        Store.EnforceImageCacheBudget(History);

        ThemeManager.Apply(Settings.Theme);
        // SystemSettingsChanged 由剪贴板监听的消息窗口线程触发（WM_SETTINGCHANGE）：只投递、不等待 UI。
        // 用 Dispatcher.Invoke 会让消息线程同步等 UI；而 UI 线程的 HotkeyService.Register 等路径又会
        // MessageWindow.Invoke 同步等消息线程 → 两边互等成环（2026-11-11 P3-1）。主题晚一拍重应用无害。
        ClipboardMonitor.SystemSettingsChanged += (_, _) =>
            Dispatcher.BeginInvoke(() => ThemeManager.Apply(Settings.Theme));

        if (args.Contains("--sample")) SampleSeeder.SeedIfEmpty(History, Store);
        WireClipboard();
        // --no-hotkeys：沙箱冒烟用——与实机实例并行时不抢 Alt+V/Alt+S，注册失败弹窗也不弹
        if (!args.Contains("--no-hotkeys")) WireHotkeys();

        // --no-tray：诊断/冒烟用——本机会话里 H.NotifyIcon 的 Shell_NotifyIcon 被拒（旧发布版同样失败），
        // 先跳过托盘让主窗口/热键能起来；其余功能不变（设置窗关闭后无托盘入口，仅测试用）
        if (!args.Contains("--no-tray")) CreateTray();
        // 托盘驻留期空闲修剪（窗口全隐藏后 WS ~173MB→30MB，实测唤起零延迟代价，见 WorkingSetTrimmer）
        _idleTrimTimer.Start();
        // 启动空闲期离屏预热面板：首唤从 ~490ms（窗口首建+首渲+首次全表重建）降到 ~130ms
        Dispatcher.BeginInvoke(PrewarmPanel, DispatcherPriority.ApplicationIdle);
        // 启动空闲期预热 UIA caret 客户端（后台 STA 线程，进程内只建一次）：把 Chrome/Electron
        // 自绘光标查询的首次冷启动成本提前付掉；不阻塞启动（2026-11-10 面板锚点三阶段落地）
        Dispatcher.BeginInvoke(Services.UiaCaretProbeThread.Shared.WarmUp, DispatcherPriority.ApplicationIdle);

        if (args.Contains("--show")) ShowPanel();
    }

    // 预热期间收到真实唤起（热键/--show/托盘）时置 false，取消排队中的隐藏回调——
    // 否则会把用户刚唤起的面板立刻藏掉
    private bool _prewarmHidePending;

    /// <summary>
    /// 启动空闲期离屏预热面板窗口：把一次性框架成本（BAML/样式首建 + D3D/JIT 首渲，实测 ~380ms）
    /// 与首次全表重建（含来源图标预热）挪到开机期付掉，首唤只剩 Show+定位（实测 491→136ms，窗口 4ms 可见）。
    /// 离屏 + 不激活 = 不闪窗不抢焦点；位置无需恢复（ShowAtCursor 每次唤起重算）。
    /// 随后的空闲修剪把工作集压回地板（实测 13MB，与未预热同族）；隐藏面板常驻的提交成本
    /// 与「首次唤起后」稳态一致——面板窗本就不在空闲关闭之列（内存优化既定决定）。
    /// </summary>
    private void PrewarmPanel()
    {
        if (_panel is not null) return; // 已被真实唤起（如 --show 启动）
        _panel = new InvokePanelWindow { ViewModel = PanelVm };
        PanelVm.RefreshFromEngine(); // 首次全表重建+图标预热：真实首唤命中版本门控直接跳过
        _panel.Left = -30000;
        _panel.Top = 0;
        _panel.ShowActivated = false;
        _prewarmHidePending = true;
        _panel.Show();
        Dispatcher.BeginInvoke(() =>
        {
            if (!_prewarmHidePending || _panel is null) return;
            _prewarmHidePending = false;
            _panel.Hide();
            _panel.ShowActivated = true; // 真实唤起要激活抢焦点
            PanelVm.IsPanelVisible = false; // 预热窗收起即隐藏态：驻留期复制不再整表重建（ShowAtCursor 重置 true）
        }, DispatcherPriority.ApplicationIdle);
    }

    private void WireClipboard()
    {
        // 入库成功即 engine.Changed → PanelViewModel（构造期订阅）整表刷新，无需再显式刷一遍
        //（旧的双重刷新 = 每次复制全列表重建两遍，2026-11-02 压测实锤）。
        // IngestClipboardAsync：载荷读取在专用 STA 读取线程（ClipboardStaReader，有超时/忙降级，
        // 不占 UI 线程），解码/落盘后台，入库经 await 续回 UI。
        // 派发用 BeginInvoke：Changed 来自剪贴板监听的消息窗口线程，同步 Invoke 会让消息线程等 UI；
        // UI 线程的 MessageWindow.Invoke（热键注册等）又会等消息线程 → 互等成环（2026-11-11 P3-1）。
        // 消息线程只投递、绝不阻塞；剪贴板事件本身经 IngestClipboardAsync 内部 gate 串行，顺序由
        // Dispatcher 队列保持，不会因异步化丢事件（只可能比瞬时读取晚一拍）。
        ClipboardMonitor.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            var ingest = _services!.GetRequiredService<ClipboardIngestService>();
            _ = ingest.IngestClipboardAsync();
        });
        if (ClipboardMonitor.Start()) return;
        // 监听挂不上（消息窗口未建成/系统拒绝）曾是完全静默的「不记录了」：记日志 + 定时重试
        //（约 1 分钟），仍失败则托盘气泡告知——核心功能失效必须可见
        CrashLog.Write("clipboard-listener",
            new InvalidOperationException("AddClipboardFormatListener 初次注册失败"), "剪贴板监听注册失败，开始定时重试");
        var retries = 0;
        var retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        retry.Tick += (_, _) =>
        {
            if (ClipboardMonitor.Start()) { retry.Stop(); return; }
            if (++retries < 12) return;
            retry.Stop();
            CrashLog.Write("clipboard-listener",
                new InvalidOperationException("重试 12 次仍失败"), "剪贴板监听注册放弃，历史记录功能不可用");
            try { _tray?.ShowNotification("Pasteup 剪贴板监听失败", "历史记录功能不可用，重启应用可重试。"); }
            catch { }
        };
        retry.Start();
    }

    private void WireHotkeys()
    {
        // HotkeyPressed 在热键消息窗口线程上触发：同样只投递、不等待 UI（Dispatcher.Invoke 会与
        // UI 线程的 MessageWindow.Invoke 互等成环——P3-1 全仓审计补漏）。热键处理晚一拍无感。
        Hotkeys.HotkeyPressed += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            if (e.Combo == Settings.InvokeHotkeyCombo) TogglePanel();
            else if (e.Combo == Settings.ScreenshotHotkeyCombo) ScreenshotService.BeginCapture();
        });
        RegisterConfiguredHotkeys();
    }

    internal void RegisterConfiguredHotkeys()
    {
        Hotkeys.UnregisterAll();
        TryRegister(Settings.InvokeHotkeyCombo, "唤起面板");
        TryRegister(Settings.ScreenshotHotkeyCombo, "截图");
    }

    private void TryRegister(KeyCombo? combo, string name)
    {
        if (combo is null) return;
        if (Hotkeys.Register(combo) is null)
            MessageBox.Show($"热键 {combo}（{name}）注册失败，可能已被其他程序占用，可在设置中更换。", "Pasteup",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CreateTray()
    {
        var invoke = Settings.InvokeHotkeyCombo?.ToString() ?? "未设置";
        var shot = Settings.ScreenshotHotkeyCombo?.ToString() ?? "未设置";
        _tray = new TaskbarIcon
        {
            ToolTipText = $"Pasteup — {invoke} 唤起 · {shot} 截图",
            Icon = AppIcon.LoadTrayIcon(),
        };
        _tray.ContextMenu = BuildTrayMenu();
        _tray.TrayLeftMouseUp += (_, _) => ShowPanel();
        // TaskbarIcon 的 Create() 只挂在 WPF Loaded 事件上；本对象纯代码创建、永不挂可视树，
        // 不显式 ForceCreate 则托盘图标从未真正注册（2026-10-27 起的隐性 bug，2026-11-01 由气泡异常暴露）。
        // Efficiency Mode 会节流后台计时器，保持关闭。
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>托盘右键菜单装配。单独成方法：POC 闸门 --poc=tray-menu 走同一条构建路径验证
    /// 「贴图列表」子菜单，避免验证代码与真实菜单逻辑分叉（2026-11-14）。</summary>
    internal System.Windows.Controls.ContextMenu BuildTrayMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();
        void Add(string header, RoutedEventHandler click, bool separator = false)
        {
            if (separator) menu.Items.Add(new System.Windows.Controls.Separator());
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += click;
            menu.Items.Add(item);
        }
        Add("唤起面板", (_, _) => ShowPanel());
        Add("截图", (_, _) => ScreenshotService.BeginCapture());
        // 「贴图列表」= 当前打开的贴图窗口（Alt+S 截图生成、钉在屏幕上、不入历史）的集中入口。
        // 贴图是瞬时状态且无标题，只能在菜单展开时实时枚举；见 RebuildPinListMenu。
        _pinListMenu = new MenuItem { Header = PinListText.Title };
        menu.Items.Add(_pinListMenu);
        // 在上下文菜单打开时重建（而非二级菜单展开时）：右键落定即已构建完，展开二级菜单无空帧
        menu.Opened += (_, _) => RebuildPinListMenu();
        Add("设置", (_, _) => ShowSettings(), separator: true);
        Add("退出", (_, _) => { IsExiting = true; Shutdown(); }, separator: true);
        return menu;
    }

    /// <summary>
    /// 托盘「贴图列表」子菜单：按 PinCoordinator 当前打开的贴图窗口实时重建。
    /// 贴图窗口没有标题、也不持久化（关掉即散），只能用「打开顺序 + 原始物理尺寸 + 缩略图」辨认；
    /// 点击一项 = 把该贴图激活置前，末尾「全部关闭」一次收掉。0 张时给一行禁用提示——
    /// 原来的占位实现只弹「当前贴图 N 张」的系统 MessageBox（2026-11-14 前），看起来像坏掉的空壳。
    /// </summary>
    private void RebuildPinListMenu()
    {
        if (_pinListMenu is null) return;
        var pins = Pins.Windows;
        _pinListMenu.Items.Clear();
        _pinListMenu.Header = PinListText.Header(pins.Count);
        if (pins.Count == 0)
        {
            _pinListMenu.Items.Add(new MenuItem { Header = PinListText.EmptyHint, IsEnabled = false });
            return;
        }

        for (var i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            var item = new MenuItem
            {
                Header = PinListText.ItemLabel(i + 1, pin.NaturalWidth, pin.NaturalHeight),
                // 缩略图复用贴图窗已解码的显示源（截图链路为零解码冻结图），不额外解码/复制像素
                Icon = new Image
                {
                    Source = pin.DisplaySource,
                    Width = 28,
                    Height = 18,
                    Stretch = System.Windows.Media.Stretch.Uniform,
                },
                ToolTip = "点击把这张贴图激活并置于最前",
            };
            item.Click += (_, _) => ActivatePin(pin);
            _pinListMenu.Items.Add(item);
        }

        _pinListMenu.Items.Add(new Separator());
        var closeAll = new MenuItem { Header = PinListText.CloseAll };
        closeAll.Click += (_, _) => Pins.CloseAll();
        _pinListMenu.Items.Add(closeAll);
    }

    /// <summary>把指定贴图提到同层最前并抢键盘焦点（焦点落上后 Esc 关闭 / F9 穿透等快捷键立刻可用）。
    /// 置顶窗之间互压：先取消再恢复 Topmost 把该窗提到最前；Activate 负责前台与键盘焦点
    /// （托盘菜单点击时本进程本就持有前台，Activate 足够，无需 Alt 点放类阶梯）。</summary>
    private static void ActivatePin(PinWindow pin)
    {
        if (!pin.IsVisible) return;
        pin.Topmost = false;
        pin.Topmost = true;
        pin.Activate();
    }

    /// <summary>窗口经关闭按钮隐藏进托盘后调用：每进程首次弹气泡，告知程序仍在通知区域运行。</summary>
    internal void NotifyMinimizedToTray()
    {
        if (_trayHintShown) return;
        _trayHintShown = true;
        try
        {
            _tray?.ShowNotification("Pasteup 仍在运行",
                "已最小化到通知区域；左键单击托盘图标打开面板，右键菜单可退出。");
        }
        catch (Exception ex)
        {
            CrashLog.Write("tray-notify", ex, "托盘气泡提示失败（忽略）");
        }
    }

    internal void ShowPanel()
    {
        _prewarmHidePending = false; // 预热的隐藏回调若仍在排队，让位于真实唤起
        // 面板被 Alt+F4 / WM_CLOSE 真正 Close 过就不能复用（再 Show 抛 InvalidOperationException，
        // 热键从此只弹错误框）；IsLoaded 在 Hide() 后仍为 true，只有 Close 才变 false
        if (_panel is null || !_panel.IsLoaded)
            _panel = new InvokePanelWindow { ViewModel = PanelVm };
        // RefreshFromEngine 由 ShowAtCursor 内完成——这里再调一遍等于每次唤起整表重建两遍
        _panel.ShowAtCursor();
    }

    internal void TogglePanel()
    {
        if (_panel is { IsVisible: true }) _panel.HidePanel();
        else ShowPanel();
    }

    internal void ShowSettings()
    {
        if (_settingsWindow is null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow();
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    internal void ScheduleAutosave()
    {
        _autosaveTimer.Stop();
        _autosaveTimer.Start();
    }

    /// <summary>保存 settings + history。compact=true 时 history 走全量重写（退出/清空路径：
    /// 下次启动基档干净、无 journal 可重放）；默认走增量（一般只追加一行 journal）。
    /// 落盘为尽力而为：目录被占/被杀软锁文件等一律放弃，等待下次自动保存重试。</summary>
    internal void TrySaveAll(bool compact = false)
    {
        // gate 串行化全部保存路径（后台自动保存 / 设置窗清空 / 退出清空），tmp 互踩不可能
        lock (_saveGate)
        {
            SaveHistoryWithImageCleanup(compact);
            try { SettingsStore.Save(Settings); }
            catch (Exception ex) { CrashLog.Write("save-settings", ex, "settings.json 保存失败（忽略）"); }
        }
    }

    /// <summary>history 落盘 + 「删除即删盘」：仅在落盘成功后排空引擎待删队列并删除对应图像文件
    ///（失败时 json 仍引用这些文件，删了会坏缩略图；队列保留待下次成功保存再删）。须在 _saveGate 内调用。
    /// compact=false 走增量保存：删除/清空/首次/超阈值时 Store 内部自动退回全量，语义等价。</summary>
    private void SaveHistoryWithImageCleanup(bool compact = false)
    {
        try
        {
            if (compact) Store.Save(History); // 全量 compaction（退出路径）
            else if (!Store.SaveIncremental(History)) return; // 本轮未落盘：不排空待删队列
            foreach (var file in History.DrainPendingImageDeletes())
                Store.DeleteImageFile(file);
        }
        catch (Exception ex) { CrashLog.Write("save-history", ex, "history.json 保存失败（忽略）"); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        // 退出路径任何异常都会把「正常退出」变成「崩溃退出」（2026-10-26 曾因此崩在 GetRequiredService），
        // 每步各自兜底：能保存的先保存，后续清理失败不影响进程以 0 退出
        if (_services is null) // POC/辅助模式未构建容器，直接退出
        {
            base.OnExit(e);
            return;
        }
        try
        {
            if (Settings.ClearHistoryOnExit)
            {
                // 清空语义：直写曾绕过 _saveGate，与在途后台保存互踩 tmp 会让退出清空静默失效；
                // 落盘成功后一并删除被清条目的图像文件（Clear 已把它们记入待删队列）。
                // 清空必须全量：下次启动基档干净、journal 置空
                History.Clear(includePinned: false);
                lock (_saveGate) SaveHistoryWithImageCleanup(compact: true);
            }
            else
            {
                // 退出做一次全量 compaction：把本次会话的 journal 合并进基档，下次启动基档干净
                TrySaveAll(compact: true);
            }
        }
        catch (Exception ex) { CrashLog.Write("exit-save", ex, "退出时保存失败（忽略）"); }
        try { Pins.CloseAll(); } catch (Exception ex) { CrashLog.Write("exit-pins", ex, "退出时关闭贴图失败（忽略）"); }
        try { _tray?.Dispose(); } catch (Exception ex) { CrashLog.Write("exit-tray", ex, "退出时释放托盘图标失败（忽略）"); }
        try
        {
            _services?.GetService<HotkeyService>()?.Dispose();
            _services?.GetService<ClipboardMonitor>()?.Dispose();
            _services?.Dispose();
        }
        catch (Exception ex) { CrashLog.Write("exit-dispose", ex, "退出时释放服务失败（忽略）"); }
        try { _instanceGuard?.Dispose(); } catch (Exception ex) { CrashLog.Write("exit-instance", ex, "退出时释放单实例守卫失败（忽略）"); }
        base.OnExit(e);
    }
}
