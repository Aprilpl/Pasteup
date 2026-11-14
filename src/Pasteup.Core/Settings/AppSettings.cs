using System.Text.Json;
using System.Text.Json.Serialization;
using Pasteup.Core.Hotkeys;

namespace Pasteup.Core.Settings;

public enum ThemeMode { System, Light, Dark }

/// <summary>
/// 面板锚点来源（2026-11-10）：Caret = 优先贴文本插入光标、取不到才跟鼠标；
/// Cursor = 始终跟鼠标。Ditto 的「At Caret / At Cursor」同款设计——降级链对用户可见，
/// 出意外时可自解释（自绘光标应用如部分 Electron 窗口只能跟鼠标）。
/// </summary>
public enum PanelAnchorMode { Caret = 0, Cursor = 1 }

public sealed class AppSettings
{
    // 默认热键：录制失效（2026-11-13 修复）时可能落盘非法组合（如「Ctrl+VKA2」= 把修饰键本身当主键），
    // 加载时据此回退。集中成常量，属性默认值与 SanitizeHotkeys 共用，避免两处漂移。
    public const string DefaultInvokeHotkey = "Alt+V";
    public const string DefaultScreenshotHotkey = "Alt+S";

    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string InvokeHotkey { get; set; } = DefaultInvokeHotkey;
    public string ScreenshotHotkey { get; set; } = DefaultScreenshotHotkey;
    public bool PasteOnEnter { get; set; } = true;
    public bool SummonResetToTop { get; set; } = true;
    public PanelAnchorMode AnchorMode { get; set; } = PanelAnchorMode.Caret;
    public int HistoryCapacity { get; set; } = 1000;
    public int RetentionDays { get; set; } = 90;
    public bool ClearHistoryOnExit { get; set; }
    public double PinDefaultOpacity { get; set; } = 1.0;
    public int PinZoomStepPercent { get; set; } = 10;

    // 窗口 DIP 尺寸记忆（2026-11-04）：0 = 未记忆（回退窗口默认）；显示器相关上限不在存储侧钳，
    // 还原时 WindowLimits 按所在屏工作区动态压回
    public double PanelWidth { get; set; }
    public double PanelHeight { get; set; }
    public double SettingsWidth { get; set; }
    public double SettingsHeight { get; set; }

    public KeyCombo? InvokeHotkeyCombo => KeyCombo.Parse(InvokeHotkey);
    public KeyCombo? ScreenshotHotkeyCombo => KeyCombo.Parse(ScreenshotHotkey);

    /// <summary>
    /// 把无法解析的热键串回退为默认值、其余设置原样保留。
    /// 只为旧录制 bug 的遗留数据兜底（修复前按 Ctrl 会被录成「Ctrl+VKA2」这类非法组合并落盘）；
    /// 与 IsValid 的「整体回退」区分开：热键是用户可达的坏状态，不值得连主题/容量/窗口尺寸一起丢。
    /// 空串/null 同样回退（Parse 返回 null）。
    /// </summary>
    public void SanitizeHotkeys()
    {
        if (InvokeHotkeyCombo is null) InvokeHotkey = DefaultInvokeHotkey;
        if (ScreenshotHotkeyCombo is null) ScreenshotHotkey = DefaultScreenshotHotkey;
    }

    public bool IsValid() =>
        InvokeHotkeyCombo is not null
        && ScreenshotHotkeyCombo is not null
        // STJ 数字枚举不做范围校验：settings.json 可被篡改成 99，越界值必须在入口挡住并整体回退默认
        && Enum.IsDefined(Theme)
        && Enum.IsDefined(AnchorMode)
        && HistoryCapacity is > 0 and <= 100_000
        && RetentionDays is > 0 and <= 3650
        && PinDefaultOpacity is > 0 and <= 1.0
        && PinZoomStepPercent is > 0 and <= 100;
}

/// <summary>settings.json 持久化；损坏文件备份后回退默认值。</summary>
public sealed class SettingsStore(string dataDirectory)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private string SettingsPath => Path.Combine(dataDirectory, "settings.json");

    public AppSettings Load()
    {
        DeleteStaleTmp(); // 写一半被杀残留的 tmp 顺手清掉（单实例保证无并发写者）
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOpts);
            if (s is null) return new AppSettings();
            s.SanitizeHotkeys(); // 旧录制 bug 遗留的非法热键只回退热键字段，不整体丢设置
            if (!s.IsValid()) return new AppSettings();
            return s;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            try { File.Move(SettingsPath, SettingsPath + ".corrupt", overwrite: true); } catch { }
            return new AppSettings();
        }
    }

    /// <summary>清掉残留的 settings.json.tmp。</summary>
    private void DeleteStaleTmp()
    {
        try { if (File.Exists(SettingsPath + ".tmp")) File.Delete(SettingsPath + ".tmp"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(dataDirectory);
        var tmp = SettingsPath + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(JsonSerializer.SerializeToUtf8Bytes(settings, JsonOpts));
            fs.Flush(true); // fsync 后再 rename（断电防半文件，与 HistoryStore.Save 同款）
        }
        File.Move(tmp, SettingsPath, overwrite: true);
    }
}
