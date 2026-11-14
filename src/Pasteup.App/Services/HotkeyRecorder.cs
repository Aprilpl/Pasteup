using System.Windows.Input;
using Pasteup.Core.Hotkeys;

namespace Pasteup.App.Services;

/// <summary>
/// 设置页热键录制：把一次 WPF 按键事件折算为可注册的 <see cref="KeyCombo"/>（纯函数，便于单测）。
///
/// 2026-11-13 修复的背景：原实现直接拿 <see cref="KeyEventArgs.Key"/> + <c>Keyboard.Modifiers</c> 造
/// 组合并立即结束录制，导致按 Ctrl 的那一刻就被当成「Ctrl + VK_LCONTROL」录了下来（Ctrl 也是
/// KeyDown），随后再按字母已无录制状态——表现为「无法设置 Ctrl+字母」。规则因此定为：
/// 1) 修饰键本身（Ctrl/Alt/Shift/Win 左右键）不参与组合，录制中只视作「按住」，继续等待主键；
/// 2) Alt 组合在 WPF 里上报为 <see cref="Key.System"/>，真键在 <see cref="KeyEventArgs.SystemKey"/>；
/// 3) 裸键（无修饰键）不构成全局热键（RegisterHotKey 语义），返回 null 让录制继续等待；
/// 4) 无法映射为虚拟键的键（None / DeadCharProcessed / ImeProcessed 等）返回 null。
/// </summary>
public static class HotkeyRecorder
{
    /// <summary>是否为修饰键本身。左/右 Ctrl、Alt、Shift、Win。</summary>
    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or
        Key.LWin or Key.RWin;

    /// <summary>
    /// 由一次按键事件折算组合键。参数对应 <see cref="KeyEventArgs.Key"/>、<see cref="KeyEventArgs.SystemKey"/>
    /// 与 <c>Keyboard.Modifiers</c>。返回 null 表示「本次按键不构成有效热键」：录制方应继续等待或取消，
    /// 不应落盘。
    /// </summary>
    public static KeyCombo? FromKeyEvent(Key key, Key systemKey, ModifierKeys modifiers)
    {
        var effective = key == Key.System ? systemKey : key;
        if (IsModifierKey(effective)) return null;
        if (effective is Key.None or Key.DeadCharProcessed or Key.ImeProcessed) return null;

        var mods = ToKeyModifiers(modifiers);
        if (mods == KeyModifiers.None) return null;

        var vk = KeyInterop.VirtualKeyFromKey(effective);
        if (vk is <= 0 or > 0xFF) return null;

        var combo = new KeyCombo(mods, vk);
        if (!combo.IsValid) return null;
        // 闭环守卫：组合必须以 ToString→Parse 原样往返（持久化/注册都走字符串），
        // 否则像「Ctrl+VKBA」这类无名称键会落盘成不可解析值、热键静默失效。
        return KeyCombo.Parse(combo.ToString()) == combo ? combo : null;
    }

    /// <summary>WPF 修饰键状态 → 持久化/注册用修饰键位。</summary>
    public static KeyModifiers ToKeyModifiers(ModifierKeys modifiers)
    {
        var mods = KeyModifiers.None;
        if ((modifiers & ModifierKeys.Control) != 0) mods |= KeyModifiers.Control;
        if ((modifiers & ModifierKeys.Alt) != 0) mods |= KeyModifiers.Alt;
        if ((modifiers & ModifierKeys.Shift) != 0) mods |= KeyModifiers.Shift;
        if ((modifiers & ModifierKeys.Windows) != 0) mods |= KeyModifiers.Win;
        return mods;
    }
}
