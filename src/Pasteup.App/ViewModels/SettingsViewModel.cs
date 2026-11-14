using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Pasteup.App.Services;
using Pasteup.App.Theme;
using Pasteup.Core.History;
using Pasteup.Core.Hotkeys;
using Pasteup.Core.Settings;
using ThemeMode = Pasteup.Core.Settings.ThemeMode;

namespace Pasteup.App.ViewModels;

/// <summary>设置视图模型：直接转发到全局 AppSettings，每次变更即持久化并应用。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly App _app;
    private AppSettings S => _app.Settings;

    public SettingsViewModel() => _app = App.Current;

    public bool PasteOnEnter
    {
        get => S.PasteOnEnter;
        set { S.PasteOnEnter = value; Save(); OnPropertyChanged(); }
    }

    public bool SummonResetToTop
    {
        get => S.SummonResetToTop;
        set { S.SummonResetToTop = value; Save(); OnPropertyChanged(); }
    }

    /// <summary>开机自启（2026-11-14）：写/删 HKCU Run 项。注册表不可写时弹窗可见化并回弹 UI——
    /// 设置项只在注册成功后落盘，避免「设置显示已开启、实际开机不会启动」的假成功。</summary>
    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            if (S.StartWithWindows == value) return;
            try
            {
                StartupRegistration.Default.Apply(value, StartupRegistration.CurrentExePath);
            }
            catch (Exception ex)
            {
                CrashLog.Write("startup-registration", ex, "开机自启启用/关闭失败（注册表不可写）");
                MessageBox.Show($"开机自启设置失败：{ex.Message}", "Pasteup",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                OnPropertyChanged(); // 回弹：设置未改，复选框复原
                return;
            }
            S.StartWithWindows = value;
            Save();
            OnPropertyChanged();
        }
    }

    /// <summary>面板锚点来源（2026-11-10）：Caret 优先贴文本光标、取不到跟鼠标；Cursor 始终跟鼠标。</summary>
    public PanelAnchorMode AnchorMode
    {
        get => S.AnchorMode;
        set
        {
            if (S.AnchorMode == value) return;
            S.AnchorMode = value;
            Save();
            OnPropertyChanged();
        }
    }

    public string InvokeHotkey
    {
        get => S.InvokeHotkey;
        set { S.InvokeHotkey = value; ApplyHotkeys(); Save(); OnPropertyChanged(); }
    }

    public string ScreenshotHotkey
    {
        get => S.ScreenshotHotkey;
        set { S.ScreenshotHotkey = value; ApplyHotkeys(); Save(); OnPropertyChanged(); }
    }

    public string InvokeHotkeyDisplay => S.InvokeHotkeyCombo?.ToString() ?? "无效组合";
    public string ScreenshotHotkeyDisplay => S.ScreenshotHotkeyCombo?.ToString() ?? "无效组合";

    public int HistoryCapacity
    {
        get => S.HistoryCapacity;
        set { S.HistoryCapacity = Math.Clamp(value, 10, 100_000); _app.History.Capacity = S.HistoryCapacity; Save(); OnPropertyChanged(); }
    }

    public int RetentionDays
    {
        get => S.RetentionDays;
        set { S.RetentionDays = Math.Clamp(value, 1, 3650); _app.History.RetentionDays = S.RetentionDays; Save(); OnPropertyChanged(); }
    }

    public bool ClearHistoryOnExit
    {
        get => S.ClearHistoryOnExit;
        set { S.ClearHistoryOnExit = value; Save(); OnPropertyChanged(); }
    }

    public double PinDefaultOpacity
    {
        get => S.PinDefaultOpacity;
        set { S.PinDefaultOpacity = Math.Clamp(value, 0.2, 1.0); Save(); OnPropertyChanged(); }
    }

    public int PinZoomStepPercent
    {
        get => S.PinZoomStepPercent;
        set { S.PinZoomStepPercent = Math.Clamp(value, 1, 100); Save(); OnPropertyChanged(); }
    }

    public string DataDirectory => _app.Store.DataDirectory;
    public string VersionText => $"v{typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"} · .NET 10 WPF";

    // 防抖后台保存：每个 setter 失焦即改，同步全量序列化+落盘在千条历史时 100-300ms×每次
    // 且与后台自动保存抢 _saveGate；1.5s 防抖（ScheduleAutosave）后由后台任务统一存
    // settings+history，退出路径 TrySaveAll 兜底冲刷未落盘的尾巴（2026-11-04 审计 C4）
    public void Save() => _app.ScheduleAutosave();

    private void ApplyHotkeys() => _app.RegisterConfiguredHotkeys();

    public void RefreshHotkeyDisplay()
    {
        OnPropertyChanged(nameof(InvokeHotkeyDisplay));
        OnPropertyChanged(nameof(ScreenshotHotkeyDisplay));
    }

    /// <summary>尝试设置新热键组合；被占用时返回 false（不落盘）。</summary>
    public bool TrySetHotkey(bool invoke, KeyCombo combo)
    {
        var probe = _app.Hotkeys.IsConflict(combo);
        if (probe) return false;
        if (invoke) InvokeHotkey = combo.ToString();
        else ScreenshotHotkey = combo.ToString();
        return true;
    }
}
