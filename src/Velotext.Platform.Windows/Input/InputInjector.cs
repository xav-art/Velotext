// Velotext: sends synthetic keyboard input (backspaces, shortcuts, text, replays) tagged as Velotext's own.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Runtime.InteropServices;
using Velotext.Core.Input;
using Velotext.Platform.Windows.Native;

namespace Velotext.Platform.Windows.Input;

/// <summary>
/// Markers stored in the extra-info field of injected input, so the keyboard hook can tell Velotext's
/// own keystrokes and replays from the user's typing.
/// </summary>
public static class InputMarkers
{
    /// <summary>Keystrokes Velotext generates to perform an expansion.</summary>
    public const nuint Own = 0x56454C4F;

    /// <summary>User keystrokes Velotext held back and re-sends.</summary>
    public const nuint Replay = 0x56454C52;
}

/// <summary>Sends synthetic keyboard input. Abstracted so the expansion engine can be tested without a keyboard.</summary>
public interface IInputInjector
{
    /// <summary>Presses Backspace <paramref name="count"/> times.</summary>
    /// <param name="count">How many characters to erase.</param>
    /// <returns>True when every event was accepted.</returns>
    bool SendBackspaces(int count);

    /// <summary>Sends a shortcut such as Ctrl+V.</summary>
    /// <param name="modifier">Modifier virtual key.</param>
    /// <param name="key">Main virtual key.</param>
    /// <returns>True when every event was accepted.</returns>
    bool SendChord(ushort modifier, ushort key);

    /// <summary>Releases the given keys.</summary>
    /// <param name="virtualKeys">Keys to release.</param>
    void ReleaseKeys(IEnumerable<int> virtualKeys);

    /// <summary>Types text as Unicode characters.</summary>
    /// <param name="text">The text to type.</param>
    /// <returns>True when every event was accepted.</returns>
    bool TypeText(string text);

    /// <summary>Re-sends a key press and release as Velotext's own input.</summary>
    /// <param name="key">The original key.</param>
    /// <returns>True when both events were accepted.</returns>
    bool Resend(in KeyStroke key);

    /// <summary>Re-sends held-back user keys in order, marked as replays.</summary>
    /// <param name="keys">The keys to replay.</param>
    /// <returns>How many keys the system refused.</returns>
    int Replay(IReadOnlyList<KeyStroke> keys);
}

/// <summary>Sends keyboard input with <c>SendInput</c>. Each call is one atomic batch, so user typing never interleaves.</summary>
public sealed class InputInjector : IInputInjector
{
    /// <summary>Virtual key: Backspace.</summary>
    public const ushort VkBack = 0x08;

    /// <summary>Virtual key: Tab.</summary>
    public const ushort VkTab = 0x09;

    /// <summary>Virtual key: Enter.</summary>
    public const ushort VkReturn = 0x0D;

    /// <summary>Virtual key: Ctrl.</summary>
    public const ushort VkControl = 0x11;

    /// <summary>Virtual key: V.</summary>
    public const ushort VkV = 0x56;

    /// <summary>Virtual key: Z.</summary>
    public const ushort VkZ = 0x5A;

    private const uint MapVkToVsc = 0;

    /// <summary>Presses Backspace <paramref name="count"/> times.</summary>
    /// <param name="count">How many characters to erase.</param>
    /// <returns>True when every event was accepted.</returns>
    public bool SendBackspaces(int count)
    {
        if (count <= 0)
        {
            return true;
        }

        var inputs = new List<InputRecord>(count * 2);
        for (var i = 0; i < count; i++)
        {
            inputs.Add(Key(VkBack, up: false));
            inputs.Add(Key(VkBack, up: true));
        }

        return Send(inputs) == inputs.Count;
    }

    /// <summary>Sends a shortcut such as Ctrl+V: press modifier, press key, release key, release modifier.</summary>
    /// <param name="modifier">Modifier virtual key.</param>
    /// <param name="key">Main virtual key.</param>
    /// <returns>True when every event was accepted.</returns>
    public bool SendChord(ushort modifier, ushort key)
    {
        InputRecord[] inputs = [Key(modifier, up: false), Key(key, up: false), Key(key, up: true), Key(modifier, up: true)];
        return Send(inputs) == inputs.Length;
    }

    /// <summary>Releases the given keys, for example modifiers still held from a hotkey.</summary>
    /// <param name="virtualKeys">Keys to release.</param>
    public void ReleaseKeys(IEnumerable<int> virtualKeys)
    {
        ArgumentNullException.ThrowIfNull(virtualKeys);
        var inputs = virtualKeys.Select(vk => Key((ushort)vk, up: true, extended: vk is 0xA3 or 0xA5 or 0x5B or 0x5C)).ToList();
        if (inputs.Count > 0)
        {
            Send(inputs);
        }
    }

    /// <summary>Types text as Unicode characters. Line breaks become Enter and tabs become Tab.</summary>
    /// <param name="text">The text to type.</param>
    /// <returns>True when every event was accepted.</returns>
    public bool TypeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var inputs = new List<InputRecord>(text.Length * 2);
        foreach (var c in text.Replace("\r\n", "\n", StringComparison.Ordinal))
        {
            switch (c)
            {
                case '\n' or '\r':
                    inputs.Add(Key(VkReturn, up: false));
                    inputs.Add(Key(VkReturn, up: true));
                    break;
                case '\t':
                    inputs.Add(Key(VkTab, up: false));
                    inputs.Add(Key(VkTab, up: true));
                    break;
                default:
                    inputs.Add(Unicode(c, up: false));
                    inputs.Add(Unicode(c, up: true));
                    break;
            }
        }

        return Send(inputs) == inputs.Count;
    }

    /// <summary>Re-sends a key press and release exactly as originally typed, as Velotext's own input.</summary>
    /// <param name="key">The original key.</param>
    /// <returns>True when both events were accepted.</returns>
    public bool Resend(in KeyStroke key)
    {
        var extended = (key.Flags & KeyStrokeAttributes.Extended) != 0;
        InputRecord[] inputs = [Raw(key.VirtualKey, key.ScanCode, up: false, extended, InputMarkers.Own), Raw(key.VirtualKey, key.ScanCode, up: true, extended, InputMarkers.Own)];
        return Send(inputs) == inputs.Length;
    }

    /// <summary>Re-sends held-back user keys in order, marked as replays.</summary>
    /// <param name="keys">The keys to replay.</param>
    /// <returns>How many keys the system refused.</returns>
    public int Replay(IReadOnlyList<KeyStroke> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var inputs = keys.Select(k => Raw(k.VirtualKey, k.ScanCode, k.IsKeyUp, (k.Flags & KeyStrokeAttributes.Extended) != 0, InputMarkers.Replay)).ToList();
        return inputs.Count - (int)Send(inputs);
    }

    private static InputRecord Key(ushort vk, bool up, bool extended = false) =>
        Raw(vk, (int)NativeMethods.MapVirtualKeyExW(vk, MapVkToVsc, 0), up, extended, InputMarkers.Own);

    private static InputRecord Raw(int vk, int scan, bool up, bool extended, nuint marker) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                VirtualKey = (ushort)vk,
                ScanCode = (ushort)scan,
                Flags = (up ? NativeMethods.KeyeventfKeyUp : 0) | (extended ? NativeMethods.KeyeventfExtendedKey : 0),
                ExtraInfo = marker,
            },
        },
    };

    private static InputRecord Unicode(char c, bool up) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                ScanCode = c,
                Flags = NativeMethods.KeyeventfUnicode | (up ? NativeMethods.KeyeventfKeyUp : 0),
                ExtraInfo = InputMarkers.Own,
            },
        },
    };

    private static unsafe uint Send(IReadOnlyCollection<InputRecord> inputs)
    {
        if (inputs.Count == 0)
        {
            return 0;
        }

        var array = inputs as InputRecord[] ?? [.. inputs];
        fixed (InputRecord* pointer = array)
        {
            return NativeMethods.SendInput((uint)array.Length, pointer, Marshal.SizeOf<InputRecord>());
        }
    }
}
