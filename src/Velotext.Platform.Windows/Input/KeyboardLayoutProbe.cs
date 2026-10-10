// Velotext: finds hotkeys that would steal a character the keyboard layout types (AltGr combinations).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Model;
using Velotext.Platform.Windows.Native;

namespace Velotext.Platform.Windows.Input;

/// <summary>
/// Windows reports AltGr as Ctrl+Alt. On many layouts (Danish, German, French and others) Ctrl+Alt plus a key
/// types a character such as @, € or {. A global hotkey on such a combination would swallow that character
/// in every app, so Velotext refuses it.
/// </summary>
public static unsafe class KeyboardLayoutProbe
{
    private const uint DoNotChangeKeyboardState = 0x4;

    /// <summary>Returns the character that <paramref name="gesture"/> types on the active keyboard layout, or <c>null</c>.</summary>
    /// <param name="gesture">The hotkey to check.</param>
    /// <param name="layout">Keyboard layout handle, or zero for the current thread's layout.</param>
    public static string? TypedCharacter(HotkeyGesture gesture, nint layout = 0)
    {
        var modifiers = gesture.Modifiers;
        if (!modifiers.HasFlag(HotkeyModifiers.Control) || !modifiers.HasFlag(HotkeyModifiers.Alt) || modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            return null;
        }

        if (layout == 0)
        {
            layout = NativeMethods.GetKeyboardLayout(0);
        }

        var state = stackalloc byte[256];
        state[0x11] = state[0xA2] = 0x80;
        state[0x12] = state[0xA5] = 0x80;
        if (modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            state[0x10] = state[0xA0] = 0x80;
        }

        var buffer = stackalloc char[8];
        var scan = NativeMethods.MapVirtualKeyExW((uint)gesture.Key, 0, layout);
        var count = NativeMethods.ToUnicodeEx((uint)gesture.Key, scan, state, buffer, 8, DoNotChangeKeyboardState, layout);
        if (count <= 0)
        {
            return null;
        }

        var text = new string(buffer, 0, count);
        return text.Any(c => c >= ' ') ? text : null;
    }
}
