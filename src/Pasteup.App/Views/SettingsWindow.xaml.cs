using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Pasteup.App.Services;
using Pasteup.App.ViewModels;
using Pasteup.Core.Capture;
using Pasteup.Core.Hotkeys;
using PstThemeMode = Pasteup.Core.Settings.ThemeMode;

namespace Pasteup.App.Views;

public partial class SettingsWindow : Window
{
    private const string RecordingPrompt = "按下新组合…（Esc 取消）";
    private const string ConflictPrompt = "已被占用，请换一组…";

    private enum HotkeyRecording
    {
        None,
        Invoke,
        Screenshot,
    }

    private HotkeyRecording _recording;
    private bool _hotkeysSuspended;

    public SettingsViewModel Vm { get; } = new();

    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += (_, _) =>
        {
            SyncThemeButtons();
            SyncAnchorButtons();
        };
        // 切走窗口（Alt+Tab 等）即结束录制：否则胶囊停在「按下新组合…」且再次激活后任意按键都会被录
        Deactivated += (_, _) => EndRecording();
        // 录制中点窗口任意别处（标签页/标题栏/输入框）也结束录制：避免提示不可见时按键被静默录走
        PreviewMouseDown += (_, _) => EndRecording();
        // 尺寸上限：宽≤所在屏工作区 50%、高≤100%（替代旧静态 MaxHeight=720；宽旧无上限），跨屏拖动自动刷新
        LocationChanged += (_, _) => Services.WindowLimits.ApplyTo(this);
        // 8 DIP 边缘热区（窗口矩形 == 可见卡片后热区全落在卡面上，取 PinWindow 同款值）；
        // 标题栏/非交互区拖动 + 边缘拉宽高；手动引擎，不依赖系统移动循环
        Services.WindowDragging.Attach(this, 8, Services.WindowDragging.IsInteractiveControlSource);
        // 尺寸记忆（DIP）：宽度构造期还原；高度在 OnContentRendered 脱离 SizeToContent 后还原；
        // 记录经 SizeChanged + 防抖自动保存落盘（还原自身差 <1 不会误记录）
        SetCurrentValue(WidthProperty,
            WindowSizeMemory.Restore(App.Current.Settings.SettingsWidth, Width, MinWidth));
        SizeChanged += (_, _) => { if (IsLoaded) RememberSettingsSize(); };
    }

    private void RememberSettingsSize()
    {
        var s = App.Current.Settings;
        if (WindowSizeMemory.Record(ActualWidth, ActualHeight, s.SettingsWidth, s.SettingsHeight) is { } sz)
        {
            s.SettingsWidth = sz.W;
            s.SettingsHeight = sz.H;
            App.Current.ScheduleAutosave();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Monitors.Invalidate();
        Services.DisplayChangeHook.Attach(this);
        // 圆角与阴影交给系统（同预览窗/清空确认窗；旧分层窗靠 WPF 自绘，从未请求过系统圆角）
        Services.Win32Input.PreferRoundedCorners(this);
        Services.Win32Input.EnableDwmShadow(this);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // 允许交互调整高度：脱离 SizeToContent，否则纵向拖动被内容测量高度顶回
        if (SizeToContent != SizeToContent.Manual)
            SizeToContent = SizeToContent.Manual;
        Services.WindowLimits.ApplyTo(this); // 首渲染 ActualWidth 就绪后再刷一次上限（先抬 Max 再还原高度）
        var memoH = WindowSizeMemory.Restore(App.Current.Settings.SettingsHeight, 0, MinHeight);
        if (memoH > 0) SetCurrentValue(HeightProperty, memoH);
        // 首开不还原高度时 SizeChanged 不触发，布局完成后显式记一次（还原路径差 <1 自然静默）
        Dispatcher.BeginInvoke(RememberSettingsSize, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnClose(object sender, RoutedEventArgs e) => HideToTray();

    /// <summary>关闭 = 最小化到通知区域：隐藏窗口不销毁（复用实例），程序常驻托盘；退出走托盘菜单。</summary>
    private void HideToTray()
    {
        EndRecording(); // 录制中关闭：先恢复胶囊显示，避免下次打开停在提示态
        Hide();
        App.Current.NotifyMinimizedToTray();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        // 拦 Alt+F4 / 系统菜单关闭；应用退出路径（IsExiting）放行真关闭。
        // 隐藏状态下被程序 Close()（空闲深度清理）也放行：用户点 ×/Esc 必然发生在可见时，
        // 隐藏窗口上的 Close 只可能来自清理意图（真关闭后 App._settingsWindow 由 Closed 置 null）
        if (e.Cancel || App.Current.IsExiting || !IsVisible) return;
        e.Cancel = true;
        HideToTray();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recording != HotkeyRecording.None)
        {
            // 录制期间吞掉一切按键（阻止 Tab 移焦点、Space/Enter 触发按钮等）
            e.Handled = true;
            if (e.Key == Key.Escape)
            {
                EndRecording(); // 取消，恢复显示当前组合
                return;
            }
            // 修饰键本身 / 裸键 / 无虚拟键：继续等待真正的组合键（原实现在这里把 Ctrl 的 KeyDown 当成了组合）
            var combo = HotkeyRecorder.FromKeyEvent(e.Key, e.SystemKey, Keyboard.Modifiers);
            if (combo is null) return;

            var invoke = _recording == HotkeyRecording.Invoke;
            var current = invoke ? App.Current.Settings.InvokeHotkeyCombo : App.Current.Settings.ScreenshotHotkeyCombo;
            var other = invoke ? App.Current.Settings.ScreenshotHotkeyCombo : App.Current.Settings.InvokeHotkeyCombo;
            if (combo == current)
            {
                EndRecording(); // 与当前组合相同：视为「保留原值」，不弹冲突
                return;
            }
            if (combo == other)
            {
                // 两个槽位不能同键：录制期自身热键已注销，RegisterHotKey 探测发现不了这种占用
                SetPrompt(ConflictPrompt);
                return;
            }
            if (!Vm.TrySetHotkey(invoke, combo))
            {
                SetPrompt(ConflictPrompt); // 被其他程序占用：保持录制、提示换一组（Esc 取消）
                return;
            }
            EndRecording(); // 绑定重新求值 → 胶囊显示新组合
            return;
        }
        if (e.Key == Key.Escape) HideToTray();
    }

    private Button? RecordingButton => _recording switch
    {
        HotkeyRecording.Invoke => HkInvoke,
        HotkeyRecording.Screenshot => HkShot,
        _ => null,
    };

    /// <summary>开始录制：先结束上一段（恢复旧提示与全局热键），再把提示写进胶囊，并临时注销自身热键。
    /// 用 SetCurrentValue 而非 Content 属性赋值——后者会覆盖 XAML 里的 OneWay 绑定，
    /// 且原 ClearValue 会把绑定一并清掉，导致录过一次后胶囊永久空白（本次一并修复）。</summary>
    private void BeginRecording(HotkeyRecording target)
    {
        EndRecording();
        _recording = target;
        SuspendHotkeys();
        SetPrompt(RecordingPrompt);
    }

    private void SetPrompt(string text) => RecordingButton?.SetCurrentValue(ContentProperty, text);

    /// <summary>结束录制并让胶囊恢复显示当前组合（刷新绑定目标），同时恢复全局热键注册。</summary>
    private void EndRecording()
    {
        if (_recording == HotkeyRecording.None) return;
        _recording = HotkeyRecording.None;
        ResumeHotkeys();
        Vm.RefreshHotkeyDisplay();
    }

    /// <summary>录制期临时注销本程序自身热键：否则按到「已注册」的旧组合会先触发它（面板/截图层直接弹出），
    /// 录制窗口收不到按键、录制被意外打断且无提示（2026-11-13 独立复核探针实证）。</summary>
    private void SuspendHotkeys()
    {
        if (_hotkeysSuspended) return;
        _hotkeysSuspended = true;
        App.Current.Hotkeys.UnregisterAll();
    }

    private void ResumeHotkeys()
    {
        if (!_hotkeysSuspended) return;
        _hotkeysSuspended = false;
        App.Current.RegisterConfiguredHotkeys();
    }

    private void OnRecordInvoke(object sender, RoutedEventArgs e) => BeginRecording(HotkeyRecording.Invoke);

    private void OnRecordShot(object sender, RoutedEventArgs e) => BeginRecording(HotkeyRecording.Screenshot);

    private void OnThemePick(object sender, RoutedEventArgs e)
    {
        var mode = sender == ThemeLight ? PstThemeMode.Light
            : sender == ThemeDark ? PstThemeMode.Dark
            : PstThemeMode.System;
        App.Current.Settings.Theme = mode;
        Theme.ThemeManager.Apply(mode);
        Vm.Save();
        SyncThemeButtons();
    }

    /// <summary>面板锚点来源（2026-11-10）：跟随光标 / 跟随鼠标；与主题三钮同款分段按钮。</summary>
    private void OnAnchorPick(object sender, RoutedEventArgs e)
    {
        Vm.AnchorMode = sender == AnchorCaret
            ? Pasteup.Core.Settings.PanelAnchorMode.Caret
            : Pasteup.Core.Settings.PanelAnchorMode.Cursor;
        SyncAnchorButtons();
    }

    private void SyncAnchorButtons()
    {
        var mode = App.Current.Settings.AnchorMode;
        SetSeg(AnchorCaret, mode == Pasteup.Core.Settings.PanelAnchorMode.Caret);
        SetSeg(AnchorCursor, mode == Pasteup.Core.Settings.PanelAnchorMode.Cursor);
    }

    private void SyncThemeButtons()
    {
        var mode = App.Current.Settings.Theme;
        SetSeg(ThemeLight, mode == PstThemeMode.Light);
        SetSeg(ThemeDark, mode == PstThemeMode.Dark);
        SetSeg(ThemeSystem, mode == PstThemeMode.System);
    }

    /// <summary>分段钮选中态交给 <see cref="SegmentButtons"/>：只挂资源引用，主题切换后自动跟着换色。</summary>
    private static void SetSeg(Button b, bool active) => SegmentButtons.SetActive(b, active);

    private void OnNavGeneral(object sender, RoutedEventArgs e) => Nav(PageGeneral, TabGeneral);
    private void OnNavHotkeys(object sender, RoutedEventArgs e) => Nav(PageHotkeys, TabHotkeys);
    private void OnNavPin(object sender, RoutedEventArgs e) => Nav(PagePin, TabPin);
    private void OnNavHistory(object sender, RoutedEventArgs e) => Nav(PageHistory, TabHistory);
    private void OnNavAbout(object sender, RoutedEventArgs e) => Nav(PageAbout, TabAbout);

    private void Nav(StackPanel page, Button tab)
    {
        EndRecording(); // 切页即取消录制（提示不可见时按键会被静默录走；鼠标路径同由 PreviewMouseDown 兜底）
        foreach (var (p, t) in new[] { (PageGeneral, TabGeneral), (PageHotkeys, TabHotkeys), (PagePin, TabPin), (PageHistory, TabHistory), (PageAbout, TabAbout) })
        {
            p.Visibility = ReferenceEquals(p, page) ? Visibility.Visible : Visibility.Collapsed;
            t.Tag = ReferenceEquals(t, tab) ? "Active" : "";
        }
    }

    private void OnOpacityInput(object sender, RoutedEventArgs e)
        => CommitInput(sender, s => double.TryParse(s, out var v) ? v : (double?)null,
            v => Vm.PinDefaultOpacity = v, () => Vm.PinDefaultOpacity.ToString("F2"));

    private void OnZoomInput(object sender, RoutedEventArgs e)
        => CommitInput(sender, s => int.TryParse(s, out var v) ? v : (int?)null,
            v => Vm.PinZoomStepPercent = v, () => Vm.PinZoomStepPercent.ToString());

    private void OnCapacityInput(object sender, RoutedEventArgs e)
        => CommitInput(sender, s => int.TryParse(s, out var v) ? v : (int?)null,
            v => Vm.HistoryCapacity = v, () => Vm.HistoryCapacity.ToString());

    private void OnRetentionInput(object sender, RoutedEventArgs e)
        => CommitInput(sender, s => int.TryParse(s, out var v) ? v : (int?)null,
            v => Vm.RetentionDays = v, () => Vm.RetentionDays.ToString());

    /// <summary>失焦提交：解析成功写入 VM（VM 内钳制），文本一律回写 VM 当前值（非法输入被纠正回显）。</summary>
    private static void CommitInput<T>(object sender, Func<string, T?> parse, Action<T> set, Func<string> getText) where T : struct
    {
        if (sender is not TextBox tb) return;
        if (parse(tb.Text) is { } v) set(v);
        tb.Text = getText();
    }

    private void OnOpenDataDir(object sender, RoutedEventArgs e)
    {
        var dir = App.Current.Store.DataDirectory;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    private void OnClearHistory(object sender, RoutedEventArgs e)
    {
        var history = App.Current.History;
        // 默认清空保留置顶与收藏（引擎 Clear 豁免二者），文案数字按并集口径
        var keep = history.Query(filter: static entry => entry.Pinned || entry.Favorite).Count;
        var favorite = history.Query(filter: static entry => entry.Favorite).Count;
        var dialog = new ClearConfirmWindow(history.Count, keep, favorite) { Owner = this };
        ModalScrim.Visibility = Visibility.Visible;
        try
        {
            if (dialog.ShowDialog() == true)
            {
                // 复选框「清空置顶/收藏的项目」：不勾（默认）= 保留置顶与收藏；勾选 = 全清。
                // 走 TrySaveAll（_saveGate 内）：与在途后台保存互斥防 tmp 互踩，且顺带删除被清条目的图像文件
                history.Clear(includePinned: dialog.ClearPinned);
                App.Current.TrySaveAll();
            }
        }
        finally
        {
            ModalScrim.Visibility = Visibility.Collapsed;
        }
    }
}
