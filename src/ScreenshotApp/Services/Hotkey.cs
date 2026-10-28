using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace ScreenshotApp.Services;

/// <summary>What a global hotkey can trigger.</summary>
internal enum HotkeyAction { FullScreen, Window, Region, RegionRepeat }

internal static class HotkeyActions
{
    public static readonly HotkeyAction[] All = Enum.GetValues<HotkeyAction>();

    public static string DefaultHotkey(HotkeyAction action) => action switch
    {
        HotkeyAction.FullScreen => "Ctrl+Shift+Print Screen",
        HotkeyAction.Window => "Alt+Print Screen",
        HotkeyAction.Region => "Print Screen",
        _ => "Shift+Print Screen",
    };

    public static string Label(HotkeyAction action) => action switch
    {
        HotkeyAction.FullScreen => "Full screen (monitor under the cursor)",
        HotkeyAction.Window => "Window",
        HotkeyAction.Region => "Region",
        _ => "Region (repeat the last one)",
    };
}

/// <summary>A modifier combination plus one key, stored in settings as text such as "Alt+Print Screen".</summary>
internal readonly record struct Hotkey(HOT_KEY_MODIFIERS Modifiers, uint Key)
{
    private const HOT_KEY_MODIFIERS ModifierMask =
        HOT_KEY_MODIFIERS.MOD_CONTROL | HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_SHIFT | HOT_KEY_MODIFIERS.MOD_WIN;

    private static readonly Dictionary<uint, string> Names = BuildNames();

    /// <summary>Keys that are safe as a global hotkey without a modifier (anything else would break typing).</summary>
    private static bool AllowedBare(uint key) => key is >= 0x70 and <= 0x87 or 0x2C or 0x13 or 0x91;

    /// <summary>Builds a hotkey from a pressed key; null when the key is unsupported or needs a modifier it does not have.</summary>
    public static Hotkey? Create(HOT_KEY_MODIFIERS modifiers, uint key)
    {
        modifiers &= ModifierMask;
        if (!Names.ContainsKey(key)) return null;
        if (modifiers == 0 && !AllowedBare(key)) return null;
        return new Hotkey(modifiers, key);
    }

    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var modifiers = default(HOT_KEY_MODIFIERS);
        foreach (var part in parts[..^1])
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= HOT_KEY_MODIFIERS.MOD_CONTROL; break;
                case "alt": modifiers |= HOT_KEY_MODIFIERS.MOD_ALT; break;
                case "shift": modifiers |= HOT_KEY_MODIFIERS.MOD_SHIFT; break;
                case "win": modifiers |= HOT_KEY_MODIFIERS.MOD_WIN; break;
                default: return null;
            }
        }

        foreach (var (code, name) in Names)
            if (name.Equals(parts[^1], StringComparison.OrdinalIgnoreCase))
                return Create(modifiers, code);
        return null;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HOT_KEY_MODIFIERS.MOD_CONTROL)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HOT_KEY_MODIFIERS.MOD_ALT)) parts.Add("Alt");
        if (Modifiers.HasFlag(HOT_KEY_MODIFIERS.MOD_SHIFT)) parts.Add("Shift");
        if (Modifiers.HasFlag(HOT_KEY_MODIFIERS.MOD_WIN)) parts.Add("Win");
        parts.Add(Names[Key]);
        return string.Join("+", parts);
    }

    private static Dictionary<uint, string> BuildNames()
    {
        var d = new Dictionary<uint, string>();
        for (char c = 'A'; c <= 'Z'; c++) d[c] = c.ToString();
        for (char c = '0'; c <= '9'; c++) d[c] = c.ToString();
        for (uint i = 1; i <= 24; i++) d[0x6F + i] = "F" + i;

        d[0x2C] = "Print Screen"; d[0x13] = "Pause"; d[0x91] = "Scroll Lock";
        d[0x2D] = "Insert"; d[0x2E] = "Delete"; d[0x24] = "Home"; d[0x23] = "End";
        d[0x21] = "Page Up"; d[0x22] = "Page Down";
        d[0x25] = "Left"; d[0x26] = "Up"; d[0x27] = "Right"; d[0x28] = "Down";
        d[0x20] = "Space"; d[0x09] = "Tab";
        d[0xC0] = "`"; d[0xBD] = "-"; d[0xBB] = "="; d[0xDB] = "["; d[0xDD] = "]"; d[0xDC] = "\\";
        d[0xBA] = ";"; d[0xDE] = "'"; d[0xBC] = ","; d[0xBE] = "."; d[0xBF] = "/";
        return d;
    }
}
