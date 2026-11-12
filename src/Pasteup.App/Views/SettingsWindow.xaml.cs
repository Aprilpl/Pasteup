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
    private bool _recordingInvoke;
    private bool _recordingShot;

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
        if (_recordingInvoke || _recordingShot)
        {
            e.Handled = true;
            var vk = KeyInterop.VirtualKeyFromKey(e.Key);
            var mods = (Keyboard.Modifiers & ModifierKeys.Control) != 0 ? KeyModifiers.Control : 0;
            mods |= (Keyboard.Modifiers & ModifierKeys.Alt) != 0 ? KeyModifiers.Alt : 0;
            mods |= (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? KeyModifiers.Shift : 0;
            mods |= (Keyboard.Modifiers & ModifierKeys.Windows) != 0 ? KeyModifiers.Win : 0;
            var combo = new KeyCombo(mods, vk);
            if (combo.IsValid && Vm.TrySetHotkey(_recordingInvoke, combo))
            {
                Vm.RefreshHotkeyDisplay();
            }
            _recordingInvoke = _recordingShot = false;
            HkInvoke.ClearValue(Button.ContentProperty);
            return;
        }
        if (e.Key == Key.Escape) HideToTray();
    }

    private void OnRecordInvoke(object sender, RoutedEventArgs e)
    {
        _recordingInvoke = true;
        _recordingShot = false;
        HkInvoke.Content = "按下新组合…（Esc 现为确认键）";
    }

    private void OnRecordShot(object sender, RoutedEventArgs e)
    {
        _recordingShot = true;
        _recordingInvoke = false;
        HkShot.Content = "按下新组合…";
    }

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

    private static void SetSeg(Button b, bool active)
    {
        b.Background = active
            ? (System.Windows.Media.Brush)FindRes("AccentTintBrush")
            : (System.Windows.Media.Brush)FindRes("ControlBgBrush");
        b.Foreground = active
            ? (System.Windows.Media.Brush)FindRes("AccentBrush")
            : (System.Windows.Media.Brush)FindRes("TextBrush");
    }

    private static object FindRes(string key) =>
        Application.Current.TryFindResource(key) ?? throw new InvalidOperationException($"缺少令牌 {key}");

    private void OnNavGeneral(object sender, RoutedEventArgs e) => Nav(PageGeneral, TabGeneral);
    private void OnNavHotkeys(object sender, RoutedEventArgs e) => Nav(PageHotkeys, TabHotkeys);
    private void OnNavPin(object sender, RoutedEventArgs e) => Nav(PagePin, TabPin);
    private void OnNavHistory(object sender, RoutedEventArgs e) => Nav(PageHistory, TabHistory);
    private void OnNavAbout(object sender, RoutedEventArgs e) => Nav(PageAbout, TabAbout);

    private void Nav(StackPanel page, Button tab)
    {
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
