// Velotext: a global key combination (modifiers plus one key) with display and validity rules.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;
using System.Text;

namespace Velotext.Core.Model;

/// <summary>Modifier keys of a hotkey. Values match the Windows <c>MOD_*</c> constants.</summary>
[Flags]
public enum HotkeyModifiers
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Either Alt key.</summary>
    Alt = 1,

    /// <summary>Either Ctrl key.</summary>
    Control = 2,

    /// <summary>Either Shift key.</summary>
    Shift = 4,

    /// <summary>Either Windows key.</summary>
    Windows = 8,
}

/// <summary>
/// A global key combination: modifiers plus one key, identified by its Windows virtual-key code.
/// </summary>
/// <param name="Modifiers">Modifier keys that must be held.</param>
/// <param name="Key">Windows virtual-key code of the main key.</param>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int Key)
{
    private const int VkF1 = 0x70;
    private const int VkF24 = 0x87;

    private static readonly Dictionary<int, string> s_keyNames = BuildKeyNames();

    /// <summary>True when the key is a function key (F1 to F24), which may be used without Ctrl, Alt or Win.</summary>
    public bool IsFunctionKey => Key is >= VkF1 and <= VkF24;

    /// <summary>
    /// Why this gesture cannot be used as a global hotkey, or <c>null</c> when it can.
    /// Typing keys need Ctrl, Alt or Win, otherwise the hotkey would swallow normal typing.
    /// </summary>
    public string? ValidationError
    {
        get
        {
            if (Key <= 0 || Key > 0xFE)
            {
                return "Choose a key.";
            }

            if (IsModifierKey(Key))
            {
                return "Add a key that is not Ctrl, Alt, Shift or Win.";
            }

            var hasCommandModifier = (Modifiers & (HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Windows)) != 0;
            return hasCommandModifier || IsFunctionKey
                ? null
                : "Add Ctrl, Alt or Win. Only F1 to F24 can be used on their own.";
        }
    }

    /// <summary>Returns true when <paramref name="virtualKey"/> is a Shift, Ctrl, Alt or Win key.</summary>
    /// <param name="virtualKey">Windows virtual-key code.</param>
    public static bool IsModifierKey(int virtualKey) =>
        virtualKey is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C;

    /// <summary>Returns a display name for a virtual-key code, for example <c>A</c>, <c>F5</c> or <c>Num 1</c>.</summary>
    /// <param name="virtualKey">Windows virtual-key code.</param>
    public static string KeyName(int virtualKey) =>
        s_keyNames.TryGetValue(virtualKey, out var name)
            ? name
            : string.Create(CultureInfo.InvariantCulture, $"Key 0x{virtualKey:X2}");

    /// <summary>Formats the gesture for display, for example <c>Ctrl+Alt+1</c>.</summary>
    public override string ToString()
    {
        var text = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            text.Append("Ctrl+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            text.Append("Alt+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            text.Append("Shift+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            text.Append("Win+");
        }

        return text.Append(KeyName(Key)).ToString();
    }

    private static Dictionary<int, string> BuildKeyNames()
    {
        var names = new Dictionary<int, string>
        {
            [0x08] = "Backspace",
            [0x09] = "Tab",
            [0x0D] = "Enter",
            [0x13] = "Pause",
            [0x1B] = "Esc",
            [0x20] = "Space",
            [0x21] = "Page Up",
            [0x22] = "Page Down",
            [0x23] = "End",
            [0x24] = "Home",
            [0x25] = "Left",
            [0x26] = "Up",
            [0x27] = "Right",
            [0x28] = "Down",
            [0x2C] = "Print Screen",
            [0x2D] = "Insert",
            [0x2E] = "Delete",
            [0x6A] = "Num *",
            [0x6B] = "Num +",
            [0x6D] = "Num -",
            [0x6E] = "Num .",
            [0x6F] = "Num /",
            [0x91] = "Scroll Lock",
            [0xBA] = ";",
            [0xBB] = "=",
            [0xBC] = ",",
            [0xBD] = "-",
            [0xBE] = ".",
            [0xBF] = "/",
            [0xC0] = "`",
            [0xDB] = "[",
            [0xDC] = "\\",
            [0xDD] = "]",
            [0xDE] = "'",
        };

        for (var vk = 0x30; vk <= 0x39; vk++)
        {
            names[vk] = ((char)vk).ToString();
        }

        for (var vk = 0x41; vk <= 0x5A; vk++)
        {
            names[vk] = ((char)vk).ToString();
        }

        for (var vk = 0x60; vk <= 0x69; vk++)
        {
            names[vk] = string.Create(CultureInfo.InvariantCulture, $"Num {vk - 0x60}");
        }

        for (var vk = VkF1; vk <= VkF24; vk++)
        {
            names[vk] = string.Create(CultureInfo.InvariantCulture, $"F{vk - VkF1 + 1}");
        }

        return names;
    }
}
