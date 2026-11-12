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
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string InvokeHotkey { get; set; } = "Alt+V";
    public string ScreenshotHotkey { get; set; } = "Alt+S";
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
            if (s is null || !s.IsValid()) return new AppSettings();
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
