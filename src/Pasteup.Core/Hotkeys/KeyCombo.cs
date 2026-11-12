namespace Pasteup.Core.Hotkeys;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>全局热键组合（Win32 虚拟键 + 修饰键），可解析/规范化/显示，作为设置项的存储单元。</summary>
public sealed record KeyCombo(KeyModifiers Modifiers, int VirtualKey)
{
    // 键名 = 显示名（PascalCase），大小写不敏感解析
    private static readonly Dictionary<string, int> KeyNames = BuildKeyNames(StringComparer.OrdinalIgnoreCase);

    public bool IsValid => Modifiers != KeyModifiers.None && VirtualKey > 0 && VirtualKey <= 0xFF;

    /// <summary>解析 "Alt+V"、"Ctrl+Shift+S"、"Win+PrintScreen" 等；大小写与分隔空格不敏感。失败返回 null。</summary>
    public static KeyCombo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = KeyModifiers.None;
        int vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = raw.Trim();
            if (part.Length == 0) continue;
            switch (part.ToLowerInvariant())
            {
                case "alt": mods |= KeyModifiers.Alt; continue;
                case "ctrl":
                case "control": mods |= KeyModifiers.Control; continue;
                case "shift": mods |= KeyModifiers.Shift; continue;
                case "win":
                case "windows": mods |= KeyModifiers.Win; continue;
            }
            if (vk != 0) return null; // 出现两个非修饰键
            if (!KeyNames.TryGetValue(part, out vk)) return null;
        }
        if (vk == 0) return null;
        return new KeyCombo(mods, vk);
    }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        if (Modifiers.HasFlag(KeyModifiers.Control)) sb.Append("Ctrl+");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) sb.Append("Alt+");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) sb.Append("Shift+");
        if (Modifiers.HasFlag(KeyModifiers.Win)) sb.Append("Win+");
        sb.Append(NameOf(VirtualKey));
        return sb.ToString();
    }

    private static string NameOf(int vk)
    {
        foreach (var (name, code) in KeyNames)
            if (code == vk) return name;
        return $"VK{vk:X2}";
    }

    private static Dictionary<string, int> BuildKeyNames(StringComparer comparer)
    {
        var map = new Dictionary<string, int>(comparer);
        for (char c = 'A'; c <= 'Z'; c++) map[c.ToString()] = c;           // 0x41..
        for (char c = '0'; c <= '9'; c++) map[c.ToString()] = c;           // 0x30..
        for (int f = 1; f <= 24; f++) map[$"F{f}"] = 0x6F + f;
        map["Space"] = 0x20; map["Enter"] = 0x0D; map["Esc"] = 0x1B;
        map["Tab"] = 0x09; map["Backspace"] = 0x08;
        map["PrintScreen"] = 0x2C; map["ScrollLock"] = 0x91; map["Pause"] = 0x13;
        map["Insert"] = 0x2D; map["Delete"] = 0x2E; map["Home"] = 0x24; map["End"] = 0x23;
        map["PageUp"] = 0x21; map["PageDown"] = 0x22;
        map["Up"] = 0x26; map["Down"] = 0x28; map["Left"] = 0x25; map["Right"] = 0x27;
        map["Oem3"] = 0xC0;      // `
        map["Minus"] = 0xBD;     // -
        map["Plus"] = 0xBB;      // =
        map["Comma"] = 0xBC;
        map["Period"] = 0xBE;
        return map;
    }
}
