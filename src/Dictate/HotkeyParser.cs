namespace Dictate;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
    Alt = 4,
    Windows = 8
}

public enum VirtualKey : uint
{
    Escape = 0x1B,
    Space = 0x20,
    LeftWindows = 0x5B,
    RightWindows = 0x5C,
    F1 = 0x70,
    F2 = 0x71,
    F3 = 0x72,
    F4 = 0x73,
    F5 = 0x74,
    F6 = 0x75,
    F7 = 0x76,
    F8 = 0x77,
    F9 = 0x78,
    F10 = 0x79,
    F11 = 0x7A,
    F12 = 0x7B,
    F13 = 0x7C,
    F14 = 0x7D,
    F15 = 0x7E,
    F16 = 0x7F,
    F17 = 0x80,
    F18 = 0x81,
    F19 = 0x82,
    F20 = 0x83,
    F21 = 0x84,
    F22 = 0x85,
    F23 = 0x86,
    F24 = 0x87,
    LeftShift = 0xA0,
    RightShift = 0xA1,
    LeftControl = 0xA2,
    RightControl = 0xA3,
    LeftAlt = 0xA4,
    RightAlt = 0xA5
}

public sealed record HotkeyShortcut(HotkeyModifiers Modifiers, VirtualKey TriggerKey)
{
    internal bool IsTrackedModifierKey(uint virtualKey)
    {
        var modifier = ModifierForVirtualKey(virtualKey);
        return modifier != HotkeyModifiers.None && Modifiers.HasFlag(modifier);
    }

    internal bool AreModifiersPressed(IReadOnlySet<uint> pressedKeys)
    {
        return IsModifierSatisfied(HotkeyModifiers.Control, pressedKeys)
            && IsModifierSatisfied(HotkeyModifiers.Shift, pressedKeys)
            && IsModifierSatisfied(HotkeyModifiers.Alt, pressedKeys)
            && IsModifierSatisfied(HotkeyModifiers.Windows, pressedKeys);
    }

    private bool IsModifierSatisfied(HotkeyModifiers modifier, IReadOnlySet<uint> pressedKeys)
    {
        return !Modifiers.HasFlag(modifier)
            || pressedKeys.Any(key => ModifierForVirtualKey(key) == modifier);
    }

    internal static HotkeyModifiers ModifierForVirtualKey(uint virtualKey)
    {
        return virtualKey switch
        {
            0x10 or (uint)VirtualKey.LeftShift or (uint)VirtualKey.RightShift => HotkeyModifiers.Shift,
            0x11 or (uint)VirtualKey.LeftControl or (uint)VirtualKey.RightControl => HotkeyModifiers.Control,
            0x12 or (uint)VirtualKey.LeftAlt or (uint)VirtualKey.RightAlt => HotkeyModifiers.Alt,
            (uint)VirtualKey.LeftWindows or (uint)VirtualKey.RightWindows => HotkeyModifiers.Windows,
            _ => HotkeyModifiers.None
        };
    }
}

public sealed record HotkeyBinding(HotkeyShortcut Shortcut, bool Suppress);

public enum HotkeyEvent
{
    TriggerPressed,
    TriggerReleased,
    Cancelled
}

public static class HotkeyParser
{
    private static readonly IReadOnlyDictionary<string, VirtualKey> NamedKeys =
        new Dictionary<string, VirtualKey>(StringComparer.OrdinalIgnoreCase)
        {
            ["space"] = VirtualKey.Space,
            ["esc"] = VirtualKey.Escape,
            ["escape"] = VirtualKey.Escape,
            ["leftctrl"] = VirtualKey.LeftControl,
            ["leftcontrol"] = VirtualKey.LeftControl,
            ["lctrl"] = VirtualKey.LeftControl,
            ["rightctrl"] = VirtualKey.RightControl,
            ["rightcontrol"] = VirtualKey.RightControl,
            ["rctrl"] = VirtualKey.RightControl,
            ["leftshift"] = VirtualKey.LeftShift,
            ["lshift"] = VirtualKey.LeftShift,
            ["rightshift"] = VirtualKey.RightShift,
            ["rshift"] = VirtualKey.RightShift,
            ["leftalt"] = VirtualKey.LeftAlt,
            ["lalt"] = VirtualKey.LeftAlt,
            ["rightalt"] = VirtualKey.RightAlt,
            ["ralt"] = VirtualKey.RightAlt
        };

    public static HotkeyShortcut Parse(string shortcut)
    {
        if (TryParse(shortcut, out var parsed, out var error))
        {
            return parsed!;
        }

        throw new HotkeyParseException(error!);
    }

    public static bool TryParse(
        string? shortcut,
        out HotkeyShortcut? parsed,
        out string? error)
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(shortcut))
        {
            error = "hotkey.shortcut must not be empty.";
            return false;
        }

        var tokens = shortcut.Split('+', StringSplitOptions.TrimEntries);
        if (tokens.Any(string.IsNullOrWhiteSpace))
        {
            error = $"Hotkey '{shortcut}' contains an empty key.";
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        for (var index = 0; index < tokens.Length - 1; index++)
        {
            if (!TryParseModifier(tokens[index], out var modifier))
            {
                error = $"'{tokens[index]}' is not a supported hotkey modifier.";
                return false;
            }

            if (modifiers.HasFlag(modifier))
            {
                error = $"Hotkey '{shortcut}' contains a duplicate modifier.";
                return false;
            }

            modifiers |= modifier;
        }

        var triggerToken = tokens[^1];
        if (TryParseModifier(triggerToken, out _))
        {
            error = $"Hotkey '{shortcut}' needs a non-modifier trigger key.";
            return false;
        }

        if (!TryParseTrigger(triggerToken, out var trigger))
        {
            error = $"'{triggerToken}' is not a supported hotkey trigger.";
            return false;
        }

        if (trigger == VirtualKey.Escape)
        {
            error = "Escape is reserved for cancelling an active recording.";
            return false;
        }

        var triggerModifier = HotkeyShortcut.ModifierForVirtualKey((uint)trigger);
        if (triggerModifier != HotkeyModifiers.None && modifiers.HasFlag(triggerModifier))
        {
            error = $"Hotkey '{shortcut}' uses the same modifier as its trigger key.";
            return false;
        }

        parsed = new HotkeyShortcut(modifiers, trigger);
        return true;
    }

    private static bool TryParseModifier(string token, out HotkeyModifiers modifier)
    {
        modifier = Normalize(token) switch
        {
            "ctrl" or "control" => HotkeyModifiers.Control,
            "shift" => HotkeyModifiers.Shift,
            "alt" => HotkeyModifiers.Alt,
            "win" or "windows" => HotkeyModifiers.Windows,
            _ => HotkeyModifiers.None
        };

        return modifier != HotkeyModifiers.None;
    }

    private static bool TryParseTrigger(string token, out VirtualKey trigger)
    {
        var normalized = Normalize(token);
        if (NamedKeys.TryGetValue(normalized, out trigger))
        {
            return true;
        }

        if (normalized.Length == 1 && char.IsAsciiLetterOrDigit(normalized[0]))
        {
            trigger = (VirtualKey)char.ToUpperInvariant(normalized[0]);
            return true;
        }

        if (normalized.StartsWith('f')
            && int.TryParse(normalized.AsSpan(1), out var functionNumber)
            && functionNumber is >= 1 and <= 24)
        {
            trigger = (VirtualKey)(0x6F + functionNumber);
            return true;
        }

        trigger = default;
        return false;
    }

    private static string Normalize(string token)
    {
        return token.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();
    }
}

public sealed class HotkeyParseException : Exception
{
    public HotkeyParseException(string message)
        : base(message)
    {
    }
}
