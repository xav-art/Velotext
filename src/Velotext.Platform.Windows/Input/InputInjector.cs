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

    /// <summary>Re-sends a key press and release as Velotext's own input, with the modifiers the user held.</summary>
    /// <param name="key">The original key.</param>
    /// <returns>True when every event was accepted.</returns>
    bool Resend(in KeyStroke key);

    /// <summary>Re-sends held-back user keys in order, marked as replays.</summary>
    /// <param name="keys">The keys to replay.</param>
    /// <returns>How many keys the system refused.</returns>
    int Replay(IReadOnlyList<KeyStroke> keys);
}

/// <summary>
/// Sends keyboard input with <c>SendInput</c>. Each call is one atomic batch, so user typing never
/// interleaves. Re-sent and replayed keys get back the Shift, Ctrl and Alt state the user typed them
/// with: the batch presses or releases those modifiers around the key (as Velotext's own input) when
/// the current state differs, so "?" stays "?" and Shift+Enter stays Shift+Enter.
/// </summary>
public sealed class InputInjector : IInputInjector
{
    /// <summary>Virtual key: Backspace.</summary>
    public const ushort VkBack = 0x08;

    /// <summary>Virtual key: Tab.</summary>
    public const ushort VkTab = 0x09;

    /// <summary>Virtual key: Enter.</summary>
    public const ushort VkReturn = 0x0D;

    /// <summary>Virtual key: Shift.</summary>
    public const ushort VkShift = 0x10;

    /// <summary>Virtual key: Ctrl.</summary>
    public const ushort VkControl = 0x11;

    /// <summary>Virtual key: Alt.</summary>
    public const ushort VkMenu = 0x12;

    /// <summary>Virtual key: packet (a Unicode character sent by software).</summary>
    public const int VkPacket = 0xE7;

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
        var inputs = BuildResend(key, CurrentModifiers());
        return Send(inputs) == inputs.Count;
    }

    /// <summary>Re-sends held-back user keys in order, marked as replays.</summary>
    /// <param name="keys">The keys to replay.</param>
    /// <returns>How many keys the system refused.</returns>
    public int Replay(IReadOnlyList<KeyStroke> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var (inputs, keyEnds) = BuildReplay(keys, CurrentModifiers());

        // SendInput stops at the first refused event; count the replayed keys that did not go out.
        var sent = (int)Send(inputs);
        return keyEnds.Count(end => end > sent);
    }

    /// <summary>The events that re-send a key press and release with the modifiers the user held.</summary>
    /// <param name="key">The original key.</param>
    /// <param name="current">The Shift, Ctrl and Alt state right now.</param>
    internal static List<InputRecord> BuildResend(in KeyStroke key, KeyStrokeAttributes current)
    {
        var inputs = new List<InputRecord>(8);
        var state = current;
        AddWithModifiers(inputs, key with { Flags = key.Flags & ~KeyStrokeAttributes.KeyUp }, InputMarkers.Own, ref state);
        AddEvent(inputs, key with { Flags = key.Flags | KeyStrokeAttributes.KeyUp }, InputMarkers.Own);
        return inputs;
    }

    /// <summary>The events that replay held keys, and where each key's events end in the list.</summary>
    /// <param name="keys">The held keys, in order.</param>
    /// <param name="current">The Shift, Ctrl and Alt state right now.</param>
    internal static (List<InputRecord> Inputs, List<int> KeyEnds) BuildReplay(IReadOnlyList<KeyStroke> keys, KeyStrokeAttributes current)
    {
        var inputs = new List<InputRecord>(keys.Count + 8);
        var keyEnds = new List<int>(keys.Count);
        var state = current;
        foreach (var key in keys)
        {
            if (key.IsModifierKey)
            {
                state = Track(state, key);
                AddEvent(inputs, key, InputMarkers.Replay);
            }
            else if (key.IsKeyUp)
            {
                AddEvent(inputs, key, InputMarkers.Replay);
            }
            else
            {
                AddWithModifiers(inputs, key, InputMarkers.Replay, ref state);
            }

            keyEnds.Add(inputs.Count);
        }

        return (inputs, keyEnds);
    }

    private static KeyStrokeAttributes CurrentModifiers() =>
        (IsDown(VkShift) ? KeyStrokeAttributes.Shift : KeyStrokeAttributes.None)
        | (IsDown(VkControl) ? KeyStrokeAttributes.Control : KeyStrokeAttributes.None)
        | (IsDown(VkMenu) ? KeyStrokeAttributes.Alt : KeyStrokeAttributes.None);

    private static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;

    private static KeyStrokeAttributes Track(KeyStrokeAttributes state, in KeyStroke key)
    {
        var flag = key.VirtualKey switch
        {
            0x10 or 0xA0 or 0xA1 => KeyStrokeAttributes.Shift,
            0x11 or 0xA2 or 0xA3 => KeyStrokeAttributes.Control,
            0x12 or 0xA4 or 0xA5 => KeyStrokeAttributes.Alt,
            _ => KeyStrokeAttributes.None,
        };
        return key.IsKeyUp ? state & ~flag : state | flag;
    }

    /// <summary>Adds a key press, pressing or releasing Shift, Ctrl and Alt around it so they match how the user typed it.</summary>
    private static void AddWithModifiers(List<InputRecord> inputs, in KeyStroke key, nuint marker, ref KeyStrokeAttributes state)
    {
        var wanted = key.Modifiers;
        var change = wanted ^ state;
        if (key.VirtualKey == VkPacket || change == KeyStrokeAttributes.None)
        {
            AddEvent(inputs, key, marker);
            return;
        }

        (KeyStrokeAttributes Flag, ushort Vk)[] modifiers = [(KeyStrokeAttributes.Shift, VkShift), (KeyStrokeAttributes.Control, VkControl), (KeyStrokeAttributes.Alt, VkMenu)];
        foreach (var (flag, vk) in modifiers)
        {
            if ((change & flag) != 0)
            {
                inputs.Add(Key(vk, up: (wanted & flag) == 0));
            }
        }

        AddEvent(inputs, key, marker);

        // Put the modifiers back as they are now, so keys the user still holds keep working.
        foreach (var (flag, vk) in modifiers)
        {
            if ((change & flag) != 0)
            {
                inputs.Add(Key(vk, up: (state & flag) == 0));
            }
        }
    }

    private static void AddEvent(List<InputRecord> inputs, in KeyStroke key, nuint marker) =>
        inputs.Add(key.VirtualKey == VkPacket
            ? Unicode((char)key.ScanCode, key.IsKeyUp, marker)
            : Raw(key.VirtualKey, key.ScanCode, key.IsKeyUp, (key.Flags & KeyStrokeAttributes.Extended) != 0, marker));

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

    private static InputRecord Unicode(char c, bool up, nuint marker = InputMarkers.Own) => new()
    {
        Type = NativeMethods.InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardInput
            {
                ScanCode = c,
                Flags = NativeMethods.KeyeventfUnicode | (up ? NativeMethods.KeyeventfKeyUp : 0),
                ExtraInfo = marker,
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
