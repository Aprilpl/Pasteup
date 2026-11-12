using Pasteup.Core.Hotkeys;
using Xunit;

namespace Pasteup.Tests;

public class KeyComboParseTests
{
    [Theory]
    [InlineData("Alt+V", "Alt+V")]
    [InlineData("Ctrl+Shift+S", "Ctrl+Shift+S")]
    [InlineData("Win+PrintScreen", "Win+PrintScreen")]
    [InlineData("alt+v", "Alt+V")]
    [InlineData("ALT+V", "Alt+V")]
    [InlineData("Alt + V", "Alt+V")]
    [InlineData("Control+A", "Ctrl+A")]
    [InlineData("Windows+D", "Win+D")]
    [InlineData("Ctrl+0", "Ctrl+0")]
    [InlineData("Ctrl+F1", "Ctrl+F1")]
    [InlineData("Ctrl+F24", "Ctrl+F24")]
    [InlineData("Alt+Space", "Alt+Space")]
    [InlineData("Alt+Enter", "Alt+Enter")]
    [InlineData("Alt+Esc", "Alt+Esc")]
    [InlineData("Alt+Tab", "Alt+Tab")]
    [InlineData("Alt+Insert", "Alt+Insert")]
    [InlineData("Alt+Delete", "Alt+Delete")]
    [InlineData("Alt+Home", "Alt+Home")]
    [InlineData("Alt+End", "Alt+End")]
    [InlineData("Alt+PageUp", "Alt+PageUp")]
    [InlineData("Alt+PageDown", "Alt+PageDown")]
    [InlineData("Alt+Left", "Alt+Left")]
    [InlineData("Alt+Down", "Alt+Down")]
    [InlineData("Ctrl+Minus", "Ctrl+Minus")]
    [InlineData("Ctrl+Plus", "Ctrl+Plus")]
    [InlineData("Ctrl+Comma", "Ctrl+Comma")]
    [InlineData("Ctrl+Period", "Ctrl+Period")]
    [InlineData("Shift+Ctrl+Alt+Win+Q", "Ctrl+Alt+Shift+Win+Q")]
    public void Parses_and_normalizes(string input, string expected)
    {
        var combo = KeyCombo.Parse(input);
        Assert.NotNull(combo);
        Assert.Equal(expected, combo!.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+++")]
    [InlineData("Alt")]
    [InlineData("Alt+XYZ")]
    [InlineData("Alt+V+C")]
    [InlineData("Banana+F")]
    public void Rejects_invalid(string? input)
    {
        Assert.Null(KeyCombo.Parse(input));
    }

    [Fact]
    public void Lone_key_has_no_modifiers_so_is_not_a_valid_hotkey()
    {
        var combo = KeyCombo.Parse("V");
        Assert.NotNull(combo);
        Assert.False(combo!.IsValid);
    }

    [Fact]
    public void Modifier_combo_is_valid()
    {
        Assert.True(KeyCombo.Parse("Alt+V")!.IsValid);
    }

    [Theory]
    [InlineData("Alt+V", KeyModifiers.Alt, 0x56)]
    [InlineData("Ctrl+S", KeyModifiers.Control, 0x53)]
    [InlineData("Ctrl+Shift+P", KeyModifiers.Control | KeyModifiers.Shift, 0x50)]
    [InlineData("Win+PrintScreen", KeyModifiers.Win, 0x2C)]
    public void Win32_values_are_correct(string text, KeyModifiers mods, int vk)
    {
        var combo = KeyCombo.Parse(text)!;
        Assert.Equal(mods, combo.Modifiers);
        Assert.Equal(vk, combo.VirtualKey);
    }

    [Fact]
    public void Equality_by_value()
    {
        Assert.Equal(KeyCombo.Parse("Alt+V"), KeyCombo.Parse("alt+v"));
        Assert.NotEqual(KeyCombo.Parse("Alt+V"), KeyCombo.Parse("Ctrl+V"));
    }
}
