using GlassDesk.Core.Models.Settings;

namespace GlassDesk.Core.Input;

public enum InputAction { None, ToggleLauncher, HideLauncher, NextPage, PreviousPage, ToggleDesktopIcons, ToggleDockRevealLock }
public readonly record struct HotkeyChord(uint Modifiers, uint VirtualKey);

public static class HotkeyText
{
    private static readonly Dictionary<string, uint> Keys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 32, ["Enter"] = 13, ["Escape"] = 27, ["Tab"] = 9,
        ["Left"] = 37, ["Up"] = 38, ["Right"] = 39, ["Down"] = 40,
        ["Home"] = 36, ["End"] = 35, ["PageUp"] = 33, ["PageDown"] = 34,
        ["Insert"] = 45, ["Delete"] = 46,
        ["OemSemicolon"] = 186, ["OemPlus"] = 187, ["OemComma"] = 188,
        ["OemMinus"] = 189, ["OemPeriod"] = 190, ["OemQuestion"] = 191,
        ["OemTilde"] = 192, ["OemOpenBrackets"] = 219, ["OemPipe"] = 220,
        ["OemCloseBrackets"] = 221, ["OemQuotes"] = 222, ["OemBackslash"] = 226,
        ["XButton1"] = 5, ["XButton2"] = 6,
        ["WheelUp"] = 0x100, ["WheelDown"] = 0x101,
        ["Multiply"] = 106, ["Add"] = 107, ["Separator"] = 108,
        ["Subtract"] = 109, ["Decimal"] = 110, ["Divide"] = 111,
    };

    public static string DisplayFormat(string text)
    {
        if (!TryParse(text, out var definition, out _)) return text.Replace("+", " + ");
        var key = definition.Key switch
        {
            "OemSemicolon" => ";", "OemPlus" => "=", "OemComma" => ",", "OemMinus" => "-",
            "OemPeriod" => ".", "OemQuestion" => "/", "OemTilde" => "`", "OemOpenBrackets" => "[",
            "OemPipe" or "OemBackslash" => "\\", "OemCloseBrackets" => "]", "OemQuotes" => "'",
            "XButton1" => "鼠标侧键 1", "XButton2" => "鼠标侧键 2",
            "WheelUp" => "滚轮向上", "WheelDown" => "滚轮向下", _ => definition.Key,
        };
        var formatted = Format(definition);
        return formatted.Length == 0 ? "" : formatted[..^definition.Key.Length].Replace("+", " + ") + key;
    }

    public static string Format(HotkeyDefinition definition) => definition.IsBound ? definition.Describe() : "";

    public static bool TryParse(string text, out HotkeyDefinition definition, out string? error)
    {
        definition = new();
        error = null;
        if (string.IsNullOrWhiteSpace(text)) return true;
        var parts = text.Trim().Split('+', StringSplitOptions.TrimEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts[..^1])
        {
            var modifier = part.Equals("Control", StringComparison.OrdinalIgnoreCase) ? "Ctrl" : part;
            if (!seen.Add(modifier)) { error = "修饰键重复。"; return false; }
            switch (modifier.ToUpperInvariant())
            {
                case "CTRL": definition.Control = true; break;
                case "ALT": definition.Alt = true; break;
                case "SHIFT": definition.Shift = true; break;
                case "WIN": definition.Win = true; break;
                default: error = "快捷键格式应为 Ctrl+Alt+Space 或 F2 等组合。"; return false;
            }
        }
        var key = parts[^1];
        definition.Key = Keys.Keys.FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key.ToUpperInvariant();
        definition.Enabled = true;
        return TryGetChord(definition, out _, out error);
    }

    public static bool TryGetChord(HotkeyDefinition definition, out HotkeyChord chord, out string? error)
    {
        chord = default;
        error = null;
        if (!definition.Enabled) return false;
        var key = definition.Key?.Trim() ?? "";
        uint virtualKey;
        var f = 0;
        var functionKey = key.StartsWith('F') && int.TryParse(key.AsSpan(1), out f) && f is >= 1 and <= 24;
        if (functionKey) virtualKey = (uint)(f + 111);
        else if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])) virtualKey = char.ToUpperInvariant(key[0]);
        else if (!Keys.TryGetValue(key, out virtualKey)) { error = "键名无效。可使用字母、数字、F1–F24、Space、方向键等。"; return false; }
        var modifiers = (definition.Alt ? 1u : 0) | (definition.Control ? 2u : 0) | (definition.Shift ? 4u : 0) | (definition.Win ? 8u : 0);
        var sideButton = virtualKey is 5 or 6 or 0x100 or 0x101;
        var punctuation = virtualKey is >= 106 and <= 111 or >= 186 and <= 192 or >= 219 and <= 222 or 226;
        if (modifiers == 0 && !functionKey && !sideButton && !punctuation) { error = "普通按键需要 Ctrl、Alt、Shift 或 Win 修饰键，以免拦截日常输入。"; return false; }
        chord = new(modifiers, virtualKey);
        return true;
    }

    public static IEnumerable<(InputAction Action, HotkeyDefinition Definition)> Bindings(ShortcutSettings settings)
    {
        yield return (InputAction.ToggleLauncher, settings.ToggleLauncher);
        yield return (InputAction.HideLauncher, settings.HideLauncher);
        yield return (InputAction.NextPage, settings.NextPage);
        yield return (InputAction.PreviousPage, settings.PreviousPage);
        yield return (InputAction.ToggleDesktopIcons, settings.ToggleDesktopIcons);
        yield return (InputAction.ToggleDockRevealLock, settings.ToggleDockRevealLock);
    }
}
