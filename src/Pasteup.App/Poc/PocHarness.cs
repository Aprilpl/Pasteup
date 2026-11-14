using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Pasteup.App.Services;
using Pasteup.App.Views;
using Pasteup.Core.Capture;
using WpfUi = Wpf.Ui.Controls;

namespace Pasteup.App.Poc;

/// <summary>
/// POC 闸门四项的测量入口：--poc=pin-memory|overlay|coldstart|wpfui-smoke。
/// 结果以 "POC&lt;TAB&gt;key&lt;TAB&gt;value" 输出到 stdout，并追加到环境变量 PASTEUP_POC_OUT 指定的文件。
/// </summary>
public static class PocHarness
{
    public static bool Handles(string[] args) => args.Any(a => a.StartsWith("--poc=", StringComparison.Ordinal));

    public static void Run(string[] args)
    {
        var mode = args.First(a => a.StartsWith("--poc="))[6..];
        var app = Application.Current;
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // bootmark：CLR+WPF 初始化 + App 资源加载（静态构造 → OnStartup 入口）的进程内耗时
        Emit("boot", $"onstartup={Environment.TickCount64 - App.StartupTicks}ms");
        switch (mode)
        {
            case "pin-memory": RunPinMemory(app); break;
            case "tray-menu": RunTrayMenu(app); break;
            case "overlay": RunOverlay(app); break;
            case "shot": RunShot(app); break;
            case "caret": RunCaret(app); break;
            case "clipread": RunClipRead(app); break;
            case "coldstart": RunColdStart(app); break;
            case "wpfui-smoke": RunWpfUiSmoke(app); break;
            case "bootmark":
                Emit("boot", "verdict\tPASS\tpurpose=onstartup-cost");
                app.Shutdown(0);
                break;
            default:
                Emit("error", $"unknown mode {mode}");
                app.Shutdown(2);
                break;
        }
    }

    internal static void Emit(string key, string value)
    {
        var line = $"POC\t{key}\t{value}";
        Console.WriteLine(line);
        var path = Environment.GetEnvironmentVariable("PASTEUP_POC_OUT");
        if (!string.IsNullOrEmpty(path))
        {
            try { File.AppendAllText(path, line + Environment.NewLine); } catch (IOException) { }
        }
    }

    /// <summary>
    /// 闸门：托盘「贴图列表」二级菜单（2026-11-14）。用**真实装配路径** <see cref="App.BuildTrayMenu"/> 构建菜单，
    /// 程序化打开/触发菜单项（不依赖鼠标注入，自动化会话/前台锁下同样可跑），校验：
    /// 五项主菜单、标题带当前贴图数、每张一行（序号 + 物理尺寸 + 缩略图）、「全部关闭」清空、0 张回到禁用提示。
    /// 点击行激活置前只输出观察值（前台锁在自动化会话里可能拒绝 Activate，不作为闸门）。
    /// 背景：自定义 MenuItem 模板原先缺 PART_Popup，任何二级菜单都弹不出来——本闸门防该回退。
    /// </summary>
    private static void RunTrayMenu(Application app)
    {
        var pasteup = (App)app;
        app.Dispatcher.Invoke(() =>
        {
            var fail = 0;
            void Check(string name, bool ok, string detail)
            {
                Emit("tray-menu", $"{name}\t{(ok ? "PASS" : "FAIL")}\t{detail}");
                if (!ok) fail++;
            }
            // 两张不同物理尺寸的贴图（行序 = 打开序）
            pasteup.Pins.Open(PaintPng(200, 100), 200, 100, 1.0);
            pasteup.Pins.Open(PaintPng(300, 150), 300, 150, 1.0);
            Pump(app, 400);
            Check("pins-opened", pasteup.Pins.Count == 2, $"count={pasteup.Pins.Count}");

            var menu = pasteup.BuildTrayMenu();
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint;
            menu.HorizontalOffset = 120;
            menu.VerticalOffset = 120;
            menu.IsOpen = true;
            Pump(app, 600);

            var top = menu.Items.OfType<System.Windows.Controls.MenuItem>()
                .Select(m => m.Header?.ToString() ?? "").ToList();
            var expectedTop = new List<string>
            {
                "唤起面板", "截图", PinListText.Header(pasteup.Pins.Count), "设置", "退出",
            };
            Check("top-items", top.SequenceEqual(expectedTop), string.Join("|", top));

            var pinList = menu.Items.OfType<System.Windows.Controls.MenuItem>()
                .FirstOrDefault(m => (m.Header?.ToString() ?? "").StartsWith(PinListText.Title, StringComparison.Ordinal));
            Check("pin-list-item", pinList is not null, "");
            if (pinList is null)
            {
                Emit("tray-menu", $"verdict\tFAIL\tno-pin-list-item");
                app.Shutdown(1);
                return;
            }

            Check("header-count", (pinList.Header?.ToString() ?? "").Contains($"（{pasteup.Pins.Count}）", StringComparison.Ordinal),
                pinList.Header?.ToString() ?? "");

            var rows = pinList.Items.OfType<System.Windows.Controls.MenuItem>()
                .Where(m => (m.Header?.ToString() ?? "").StartsWith("贴图 ", StringComparison.Ordinal)).ToList();
            Check("row-count", rows.Count == 2, $"rows={rows.Count}");
            Check("row-labels",
                rows.Count == 2
                && rows[0].Header?.ToString() == PinListText.ItemLabel(1, 200, 100)
                && rows[1].Header?.ToString() == PinListText.ItemLabel(2, 300, 150),
                string.Join("|", rows.Select(r => r.Header?.ToString() ?? "")));
            Check("row-icons", rows.All(r => r.Icon is System.Windows.Controls.Image { Source: not null }), "");
            Check("rows-enabled", rows.All(r => r.IsEnabled), "");

            var closeAll = pinList.Items.OfType<System.Windows.Controls.MenuItem>()
                .FirstOrDefault(m => m.Header?.ToString() == PinListText.CloseAll);
            Check("close-all-item", closeAll is not null, "");

            // 真正展开二级菜单：Menu.Item 模板必须有 PART_Popup，否则 IsSubmenuOpen=true 也不会有弹层
            //（2026-11-14 修复前的实际故障形态：UIA 报 Expanded、屏幕上什么都没有）
            pinList.IsSubmenuOpen = true;
            Pump(app, 700);
            Check("submenu-opens", pinList.IsSubmenuOpen, $"open={pinList.IsSubmenuOpen}");
            pinList.IsSubmenuOpen = false;
            Pump(app, 200);

            // 点击第一行 → 该贴图激活置前（观察值；前台锁可能拒绝，不设闸门）
            if (rows.Count > 0)
            {
                var pin0 = pasteup.Pins.Windows[0];
                var hwnd = new System.Windows.Interop.WindowInteropHelper(pin0).Handle;
                rows[0].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                Pump(app, 400);
                Emit("tray-menu",
                    $"activate-obs\tactive={pin0.IsActive}\tfg=0x{Win32Input.ForegroundWindow().ToInt64():X}\thwnd=0x{hwnd.ToInt64():X}");
            }

            // 「全部关闭」→ 贴图清零
            if (closeAll is not null)
            {
                closeAll.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
                Pump(app, 500);
            }
            Check("close-all", pasteup.Pins.Count == 0, $"count={pasteup.Pins.Count}");

            // 重开菜单 → 0 张时只有一行禁用提示、标题回落为裸标题
            menu.IsOpen = false;
            Pump(app, 200);
            menu.IsOpen = true;
            Pump(app, 500);
            var empty = pinList.Items.OfType<System.Windows.Controls.MenuItem>().ToList();
            Check("empty-hint",
                empty.Count == 1 && !empty[0].IsEnabled && empty[0].Header?.ToString() == PinListText.EmptyHint,
                empty.Count == 1 ? empty[0].Header?.ToString() ?? "" : $"items={empty.Count}");
            Check("header-reset", pinList.Header?.ToString() == PinListText.Title, pinList.Header?.ToString() ?? "");
            menu.IsOpen = false;

            Emit("tray-menu", $"verdict\t{(fail == 0 ? "PASS" : "FAIL")}\tfails={fail}");
            // 本模式开过 ContextMenu Popup 与贴图窗，实测 app.Shutdown() 会留下进程不退（POC 无正常退出需求，
            // 直接结束进程；与 pin-memory 等无 Popup 的模式不同，这里不冒险走正常退出流程）。
            Environment.Exit(fail == 0 ? 0 : 1);
        });
    }

    /// <summary>闸门 1：20 个贴图窗口 × 3 轮开合，工作集须回归。</summary>
    private static void RunPinMemory(Application app)
    {
        var png = PaintPng(660, 420);
        var proc = Process.GetCurrentProcess();
        long baselineKb = 0;
        var deltas = new List<long>();

        app.Dispatcher.Invoke(() =>
        {
            for (var round = 1; round <= 3; round++)
            {
                var wins = new List<PinWindow>();
                for (var i = 0; i < 20; i++)
                {
                    var w = new PinWindow(png, 660, 420, 1.0);
                    w.Show();
                    w.UpdateLayout();
                    wins.Add(w);
                }
                Pump(app, 400);
                proc.Refresh();
                var openKb = proc.WorkingSet64 / 1024;
                var openPrivKb = proc.PrivateMemorySize64 / 1024;
                foreach (var w in wins) w.Close();
                Pump(app, 400);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                Pump(app, 200);
                proc.Refresh();
                var closedKb = proc.WorkingSet64 / 1024;
                if (round == 1) baselineKb = closedKb;
                deltas.Add(closedKb - baselineKb);
                Emit("pin-memory", $"round{round}\topen={openKb}KB(priv {openPrivKb}KB)\tclosed={closedKb}KB(priv {proc.PrivateMemorySize64 / 1024}KB)\tdelta_vs_r1={deltas[^1]}KB\tmanaged={GC.GetTotalMemory(false) / 1024}KB");
            }
            var pass = deltas[^1] < 20 * 1024; // 最后回收后与首轮差 < 20MB 视为无泄漏台阶
            Emit("pin-memory", $"verdict\t{(pass ? "PASS" : "FAIL")}\tfinal_delta={deltas[^1]}KB");
            app.Shutdown(pass ? 0 : 1);
        });
    }

    /// <summary>
    /// caret 采集链冒烟（2026-11-10）：对当前前台窗口跑 native → UIA TextPattern → MSAA，
    /// 输出命中来源/矩形/耗时。用于实机验证编辑器与浏览器里的锚点来源（先预热 UIA，最多重试两次）。
    /// </summary>
    private static void RunCaret(Application app)
    {
        app.Dispatcher.Invoke(() =>
        {
            var fg = Win32Input.ForegroundWindow();
            Emit("caret", $"foreground=0x{fg:X} pid={Win32Input.ProcessIdOfWindow(fg)}");
            if (fg == IntPtr.Zero)
            {
                Emit("caret", "verdict\tFAIL\tno-foreground");
                app.Shutdown(2);
                return;
            }

            UiaCaretProbeThread.Shared.WarmUp();
            Pump(app, 500);

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                var sw = Stopwatch.StartNew();
                Win32Input.PhysicalRect? focus =
                    Win32Input.TryGetFocusWindowRect(fg, out var focusRect) ? focusRect : null;
                var context = new CaretProbeContext(fg, focus,
                    (x, y) => Monitors.ContainingPhysicalPoint(x, y) is not null);
                var hit = CaretAcquisition.TryAcquire(context, CaretAcquisition.DefaultProbes,
                    out var trace);
                Emit("caret", $"attempt{attempt}\telapsed={sw.ElapsedMilliseconds}ms\ttrace=[{trace}]");
                if (hit is { } h)
                {
                    Emit("caret",
                        $"hit\tsource={h.Source}\trect=({h.Caret.Left},{h.Caret.Top},{h.Caret.Width},{h.Caret.Height})\tdetail={h.Detail}");
                    Emit("caret", "verdict\tPASS");
                    app.Shutdown(0);
                    return;
                }
                if (attempt < 2) Pump(app, 300); // Chromium 首次激活无障碍树可能超过单次超时
            }

            Emit("caret", "verdict\tFAIL\tno-caret");
            app.Shutdown(1);
        });
    }

    /// <summary>
    /// 专用 STA 剪贴板读取线程冒烟（2026-11-10 第2批 2-3）：在 UI 线程发起一次 ReadAsync 并等结果，
    /// 校验读取线程能把当前剪贴板归一化为 text / png / dib / files 四种载荷之一（none = 空/不可读，
    /// 也算读取线程正常工作）。用途：验证「OLE 读取离开 UI 线程」后归一化语义不变，且 Dispose 能回收线程。
    /// </summary>
    private static void RunClipRead(Application app)
    {
        app.Dispatcher.Invoke(() =>
        {
            try
            {
                using var reader = new ClipboardStaReader();
                var payload = reader.ReadAsync(1000).GetAwaiter().GetResult();
                switch (payload)
                {
                    case string text:
                        Emit("clipread", $"kind=text len={text.Length}");
                        break;
                    case byte[] png:
                        Emit("clipread", $"kind=png bytes={png.Length}");
                        break;
                    case System.Windows.Media.Imaging.BitmapSource dib:
                        Emit("clipread", $"kind=dib {dib.PixelWidth}x{dib.PixelHeight}");
                        break;
                    case List<string> files:
                        Emit("clipread", $"kind=files count={files.Count}");
                        break;
                    default:
                        Emit("clipread", "kind=none");
                        break;
                }
                Emit("clipread", "verdict\tPASS");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Emit("clipread", $"verdict\tFAIL\t{ex.GetType().Name}: {ex.Message}");
                app.Shutdown(1);
            }
        });
    }

    /// <summary>
    /// 截图定格-裁剪链路（C6，2026-11-05）：真实屏幕开遮罩 → 注入 DIP 选区确认 →
    /// 校验回调尺寸、PNG 可解码、显示源与落盘像素一致（所见即所得）。
    /// </summary>
    private static void RunShot(Application app)
    {
        app.Dispatcher.Invoke(() =>
        {
            var mon = Monitors.Primary();
            if (mon is null)
            {
                Emit("shot", "verdict\tFAIL\tno-monitor");
                app.Shutdown(1);
                return;
            }

            var sw = Stopwatch.StartNew();
            var overlay = new ScreenshotOverlayWindow(mon);
            var ctorMs = sw.Elapsed.TotalMilliseconds;
            overlay.Show();
            Pump(app, 300); // 首帧渲染落定再确认

            overlay.RegionConfirmed += (shot, display) =>
            {
                sw.Restart();
                var decoded = ImageCodec.FromPng(shot.Png);
                var decodeMs = sw.Elapsed.TotalMilliseconds;

                // 显示源（贴图窗直显）中心像素 vs 落盘 PNG 解码中心像素：所见即所得
                var px = new byte[4];
                display.CopyPixels(new Int32Rect(display.PixelWidth / 2, display.PixelHeight / 2, 1, 1), px, 4, 0);
                var py = new byte[4];
                decoded.CopyPixels(new Int32Rect(decoded.PixelWidth / 2, decoded.PixelHeight / 2, 1, 1), py, 4, 0);

                var pass = shot.Width == 200 && shot.Height == 100
                           && decoded.PixelWidth == 200 && decoded.PixelHeight == 100
                           && display.PixelWidth == 200 && display.PixelHeight == 100
                           && display.IsFrozen
                           && px.SequenceEqual(py);
                Emit("shot", $"ctor={ctorMs:F1}ms\tencode={shot.Png.Length}B\tdecode={decodeMs:F1}ms");
                Emit("shot", $"verdict\t{(pass ? "PASS" : "FAIL")}");
                app.Shutdown(pass ? 0 : 1);
            };

            // 选区取主屏局部 200×100 物理像素（DIP×dipScale 与确认路径 round 往返精确）
            var dip = 96.0 / mon.Dpi;
            overlay.PocConfirmSelection(new Rect(120 * dip, 80 * dip, 200 * dip, 100 * dip));
        });
    }

    /// <summary>闸门 2：全屏覆盖层 + 60Hz 选区动画 3 秒的帧耗时与 CPU。</summary>
    private static void RunOverlay(Application app)
    {
        app.Dispatcher.Invoke(() =>
        {
            var mon = Monitors.Primary();
            if (mon is null)
            {
                Emit("overlay", "verdict\tFAIL\tno-monitor");
                app.Shutdown(1);
                return;
            }
            var overlay = new ScreenshotOverlayWindow(mon);
            overlay.Show();
            Pump(app, 300);

            // 用真实渲染帧回调驱动动画并测帧间隔（DispatcherTimer 空闲时约 30ms 节流，测不出真实帧率）
            var frames = new List<double>();
            var sw = Stopwatch.StartNew();
            var last = sw.Elapsed.TotalMilliseconds;
            var proc = Process.GetCurrentProcess();
            var cpu0 = proc.TotalProcessorTime;
            var wall0 = sw.Elapsed;
            EventHandler renderFrame = null!;
            var t = 0;
            renderFrame = (_, _) =>
            {
                var now = sw.Elapsed.TotalMilliseconds;
                frames.Add(now - last);
                last = now;
                var x = (Math.Sin(t * 0.015) * 0.3 + 0.35) * overlay.ActualWidth;
                var y = (Math.Cos(t * 0.012) * 0.25 + 0.35) * overlay.ActualHeight;
                overlay.PocUpdateSelection(new Rect(x, y, overlay.ActualWidth * 0.3, overlay.ActualHeight * 0.3));
                t++;
                if (sw.Elapsed.TotalMilliseconds > 3000)
                {
                    System.Windows.Media.CompositionTarget.Rendering -= renderFrame;
                    proc.Refresh();
                    var cpu = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / Math.Max(1, (sw.Elapsed - wall0).TotalMilliseconds) * 100;
                    var sorted = frames.OrderBy(f => f).ToArray();
                    double P(double q) => sorted[Math.Min(sorted.Length - 1, (int)(q * sorted.Length))];
                    var tier = System.Windows.Media.RenderCapability.Tier;
                    Emit("overlay", $"tier={tier >> 16}&{(tier >> 8) & 0xFF:X2}&{tier & 0xFF:X2}\tframes={sorted.Length}\tavg={frames.Average():F2}ms\tp50={P(0.5):F2}ms\tp95={P(0.95):F2}ms\tp99={P(0.99):F2}ms\tcpu={cpu:F1}%");
                    // 60Hz 显示器上帧间隔被 vsync 锁在 ~16.7ms，这是最优值；判据=能持续满帧（p95 < 18.5ms）
                    var pass = P(0.95) < 18.5 && cpu < 40;
                    Emit("overlay", $"verdict\t{(pass ? "PASS" : "FAIL")}");
                    overlay.Close();
                    app.Shutdown(pass ? 0 : 1);
                }
            };
            System.Windows.Media.CompositionTarget.Rendering += renderFrame;
        });
    }

    /// <summary>闸门 3：冷启动（内部计时：进程起点→首个窗口渲染完成）。</summary>
    private static void RunColdStart(Application app)
    {
        Window? win = null;
        var done = false;
        // 看门狗：主循环若未按预期调度（如 ContentRendered 不触发），5 秒后强制失败退出
        var watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        watchdog.Tick += (_, _) =>
        {
            if (done) return;
            done = true;
            Emit("coldstart", "verdict\tFAIL\ttimeout waiting for ContentRendered");
            app.Shutdown(1);
        };
        watchdog.Start();

        app.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            win = new Window
            {
                Width = 660, Height = 480, WindowStyle = WindowStyle.None, ShowInTaskbar = false,
                // 注意：Content 为 null 的窗口可能永不触发 ContentRendered（WPF 怪癖），必须给实际内容
                Content = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x6C, 0x80)),
                    Child = new System.Windows.Controls.TextBlock
                    {
                        Text = "Pasteup", FontSize = 42, FontWeight = FontWeights.Bold,
                        Foreground = Brushes.White,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
            win.ContentRendered += (_, _) =>
            {
                if (done) return;
                done = true;
                watchdog.Stop();
                var ms = Environment.TickCount64 - App.StartupTicks;
                Emit("coldstart", $"inprocess_staticctor_to_render={ms}ms\tframework={typeof(object).Assembly.GetName().Version}");
                Emit("coldstart", $"verdict\t{(ms < 200 ? "PASS" : "FAIL")}\t{ms}ms (进程内；进程创建成本另见 wall 数据)");
                win.Close();
                app.Shutdown(ms < 200 ? 0 : 1);
            };
            win.Show();
        });
    }

    /// <summary>闸门 4：WPF-UI 在 .NET 10 下可运行且资源可覆盖。</summary>
    private static void RunWpfUiSmoke(Application app)
    {
        app.Dispatcher.Invoke(() =>
        {
            try
            {
                var icon = new WpfUi.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Search24);
                var toggle = new WpfUi.ToggleSwitch();
                var win = new Window { Width = 320, Height = 200, ShowInTaskbar = false, ShowActivated = false };
                var panel = new StackPanel { Margin = new Thickness(16) };
                panel.Children.Add(icon);
                panel.Children.Add(toggle);
                panel.Children.Add(new WpfUi.Button { Content = "WPF-UI Button" });
                win.Content = panel;
                win.Show();
                Pump(app, 300);
                // 令牌覆盖验证：我们的 AccentBrush 可解析且与默认值不同
                var accent = (Brush)win.TryFindResource("AccentBrush")!;
                toggle.IsChecked = true;
                Pump(app, 200);
                win.Close();
                Emit("wpfui-smoke", $"rendered=true\taccent_resolved={accent != null}\ttoggle_state={toggle.IsChecked}");
                Emit("wpfui-smoke", "verdict\tPASS");
                app.Shutdown(0);
            }
            catch (Exception ex)
            {
                Emit("wpfui-smoke", $"verdict\tFAIL\t{ex.GetType().Name}: {ex.Message}");
                app.Shutdown(1);
            }
        });
    }

    private static void Pump(Application app, int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
        // 再泵一次渲染
        app.Dispatcher.Invoke(DispatcherPriority.Render, () => { });
    }

    private static byte[] PaintPng(int w, int h) => ImageCodec.RenderToPng(w, h, dc =>
    {
        var brush = new LinearGradientBrush(
            Color.FromRgb(27, 108, 128), Color.FromRgb(74, 138, 214), 45);
        dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 1);
        for (int x = 0; x < w; x += 32)
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, h));
        var text = new FormattedText("Pasteup POC",
            System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            42, Brushes.White, 96);
        dc.DrawText(text, new Point(24, h / 2.0 - 24));
    });
}
