using System.Windows.Input;
using Pasteup.App.Services;
using Pasteup.Core.Hotkeys;
using Xunit;

namespace Pasteup.Tests;

/// <summary>
/// 2026-11-13 修复「无法设置 Ctrl+字母」：原录制实现直接拿 KeyEventArgs.Key + Keyboard.Modifiers
/// 造组合并立即结束录制，按 Ctrl 的 KeyDown 本身就被录成 Ctrl+VK_LCONTROL，字母永远录不进去。
/// 这里锁定折算规则：修饰键不参与、Alt 走 SystemKey、裸键/无效键不产生组合。
/// </summary>
public class HotkeyRecorderTests
{
    [Theory]
    [InlineData(Key.J, ModifierKeys.Control, KeyModifiers.Control, 0x4A, "Ctrl+J")]
    [InlineData(Key.S, ModifierKeys.Control | ModifierKeys.Shift, KeyModifiers.Control | KeyModifiers.Shift, 0x53, "Ctrl+Shift+S")]
    [InlineData(Key.F12, ModifierKeys.Alt, KeyModifiers.Alt, 0x7B, "Alt+F12")]
    [InlineData(Key.S, ModifierKeys.Windows, KeyModifiers.Win, 0x53, "Win+S")]
    [InlineData(Key.D1, ModifierKeys.Control, KeyModifiers.Control, 0x31, "Ctrl+1")]
    [InlineData(Key.Q, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift, 0x51, "Ctrl+Alt+Shift+Q")]
    public void Maps_letter_digit_and_function_keys(Key key, ModifierKeys wpfMods, KeyModifiers expectedMods, int vk, string text)
    {
        var combo = HotkeyRecorder.FromKeyEvent(key, Key.None, wpfMods);

        Assert.NotNull(combo);
        Assert.Equal(expectedMods, combo!.Modifiers);
        Assert.Equal(vk, combo.VirtualKey);
        Assert.Equal(text, combo.ToString());
        Assert.True(combo.IsValid);
    }

    [Fact]
    public void Alt_combo_uses_system_key_because_wpf_reports_Key_System()
    {
        // Alt 按住时 WPF 上报 e.Key == Key.System、真键在 e.SystemKey；按 Key 直接映射会得到 VK_MENU。
        var combo = HotkeyRecorder.FromKeyEvent(Key.System, Key.J, ModifierKeys.Alt);

        Assert.NotNull(combo);
        Assert.Equal("Alt+J", combo!.ToString());
        Assert.Equal(0x4A, combo.VirtualKey);
    }

    [Fact]
    public void Pressing_ctrl_then_letter_records_ctrl_letter()
    {
        // 回归场景：先按 Ctrl（KeyDown），再按 J。第一步必须被忽略，第二步才构成组合。
        Assert.Null(HotkeyRecorder.FromKeyEvent(Key.LeftCtrl, Key.None, ModifierKeys.Control));

        var combo = HotkeyRecorder.FromKeyEvent(Key.J, Key.None, ModifierKeys.Control);

        Assert.NotNull(combo);
        Assert.Equal("Ctrl+J", combo!.ToString());
    }

    [Theory]
    [InlineData(Key.LeftCtrl, ModifierKeys.Control)]
    [InlineData(Key.RightCtrl, ModifierKeys.Control)]
    [InlineData(Key.LeftAlt, ModifierKeys.Alt)]
    [InlineData(Key.RightAlt, ModifierKeys.Alt)]
    [InlineData(Key.LeftShift, ModifierKeys.Shift)]
    [InlineData(Key.RightShift, ModifierKeys.Shift)]
    [InlineData(Key.LWin, ModifierKeys.Windows)]
    [InlineData(Key.RWin, ModifierKeys.Windows)]
    public void Modifier_keys_alone_never_form_a_combo(Key key, ModifierKeys mods)
        => Assert.Null(HotkeyRecorder.FromKeyEvent(key, Key.None, mods));

    [Fact]
    public void Alt_alone_arrives_as_System_plus_LeftAlt_and_is_rejected()
        => Assert.Null(HotkeyRecorder.FromKeyEvent(Key.System, Key.LeftAlt, ModifierKeys.Alt));

    [Fact]
    public void Bare_key_without_modifier_is_not_a_global_hotkey()
        => Assert.Null(HotkeyRecorder.FromKeyEvent(Key.J, Key.None, ModifierKeys.None));

    [Theory]
    [InlineData(Key.OemSemicolon, "Ctrl+Semicolon")]
    [InlineData(Key.OemQuestion, "Ctrl+Slash")]
    [InlineData(Key.OemOpenBrackets, "Ctrl+LeftBracket")]
    [InlineData(Key.OemPipe, "Ctrl+Backslash")]
    [InlineData(Key.OemCloseBrackets, "Ctrl+RightBracket")]
    [InlineData(Key.OemQuotes, "Ctrl+Quote")]
    [InlineData(Key.NumPad7, "Ctrl+Numpad7")]
    [InlineData(Key.Decimal, "Ctrl+NumpadDecimal")]
    public void Records_punctuation_and_numpad_keys_that_survive_the_string_round_trip(Key key, string expected)
    {
        var combo = HotkeyRecorder.FromKeyEvent(key, Key.None, ModifierKeys.Control);

        Assert.NotNull(combo);
        Assert.Equal(expected, combo!.ToString());
        Assert.Equal(combo, KeyCombo.Parse(combo.ToString()));
    }

    [Theory]
    [InlineData(Key.BrowserBack)]
    [InlineData(Key.MediaPlayPause)]
    public void Keys_without_a_parseable_name_are_rejected_instead_of_persisting_garbage(Key key)
        => Assert.Null(HotkeyRecorder.FromKeyEvent(key, Key.None, ModifierKeys.Control));

    [Theory]
    [InlineData(Key.None)]
    [InlineData(Key.DeadCharProcessed)]
    [InlineData(Key.ImeProcessed)]
    public void Unmappable_keys_are_rejected(Key key)
        => Assert.Null(HotkeyRecorder.FromKeyEvent(key, Key.None, ModifierKeys.Control));

    [Theory]
    [InlineData(ModifierKeys.None, KeyModifiers.None)]
    [InlineData(ModifierKeys.Control, KeyModifiers.Control)]
    [InlineData(ModifierKeys.Alt | ModifierKeys.Shift, KeyModifiers.Alt | KeyModifiers.Shift)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows,
        KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Win)]
    public void Maps_modifier_flags(ModifierKeys input, KeyModifiers expected)
        => Assert.Equal(expected, HotkeyRecorder.ToKeyModifiers(input));
}
