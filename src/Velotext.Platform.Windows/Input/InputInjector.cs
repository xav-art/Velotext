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
    /// <param name="keepModifierDown">
    /// True to leave the modifier pressed; the caller releases it with <see cref="ReleaseKeys"/>. Some apps
    /// check the modifier only when they get to the key, which can be later when they are busy.
    /// </param>
    /// <returns>True when every event was accepted.</returns>
    bool SendChord(ushort modifier, ushort key, bool keepModifierDown = false);

    /// <summary>Releases the given keys.</summary>
    /// <param name="virtualKeys">Keys to release.</param>
    void ReleaseKeys(IEnumerable<int> virtualKeys);

    /// <summary>Types text as Unicode characters.</summary>
    /// <param name="text">The text to type.</param>
    /// <returns>True when every event was accepted.</returns>
    bool TypeText(string text);

    /// <summary>Re-sends a key press and release as Velotext's own input, with the modifiers the user held.</summary>
    /// <param name="key">The original key.</param>
    /// <param name="physical">The modifier keys the user physically holds now; the input ends in this state.</param>
    /// <returns>True when every event was accepted.</returns>
    bool Resend(in KeyStroke key, ModifierSides physical);

    /// <summary>Re-sends held-back user keys in order, marked as replays.</summary>
    /// <param name="keys">The keys to replay.</param>
    /// <param name="physical">The modifier keys the user physically holds now; the input ends in this state.</param>
    /// <returns>How many keys the system refused.</returns>
    int Replay(IReadOnlyList<KeyStroke> keys, ModifierSides physical);
}

/// <summary>Shift, Ctrl and Alt on each side of the keyboard. Bit n is virtual key 0xA0 + n.</summary>
[Flags]
public enum ModifierSides
{
    /// <summary>No modifier.</summary>
    None = 0,

    /// <summary>Left Shift (VK 0xA0).</summary>
    LeftShift = 1,

    /// <summary>Right Shift (VK 0xA1).</summary>
    RightShift = 2,

    /// <summary>Left Ctrl (VK 0xA2).</summary>
    LeftControl = 4,

    /// <summary>Right Ctrl (VK 0xA3).</summary>
    RightControl = 8,

    /// <summary>Left Alt (VK 0xA4).</summary>
    LeftAlt = 16,

    /// <summary>Right Alt, or AltGr (VK 0xA5).</summary>
    RightAlt = 32,
}

/// <summary>
/// Sends keyboard input with <c>SendInput</c>. Each call is one atomic batch, so user typing never
/// interleaves. Re-sent and replayed keys get back the Shift, Ctrl and Alt state the user typed them
/// with: the batch presses or releases those modifiers before the key (as Velotext's own input) when the
/// state differs, so "?" stays "?" and Shift+Enter stays Shift+Enter. Every batch ends with the modifiers
/// in the state the user physically holds, side by side, so none is left pressed.
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

    /// <summary>Virtual key: left Shift; right Shift, left and right Ctrl, left and right Alt follow (0xA1 to 0xA5).</summary>
    public const int VkLeftShift = 0xA0;

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
    /// <param name="keepModifierDown">True to leave the modifier pressed for the caller to release.</param>
    /// <returns>True when every event was accepted.</returns>
    public bool SendChord(ushort modifier, ushort key, bool keepModifierDown = false)
    {
        InputRecord[] inputs = keepModifierDown
            ? [Key(modifier, up: false), Key(key, up: false), Key(key, up: true)]
            : [Key(modifier, up: false), Key(key, up: false), Key(key, up: true), Key(modifier, up: true)];
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
    /// <param name="physical">The Shift, Ctrl and Alt keys the user physically holds now; the batch ends in this state.</param>
    /// <returns>True when every event was accepted.</returns>
    public bool Resend(in KeyStroke key, ModifierSides physical)
    {
        var inputs = BuildResend(key, CurrentModifiers(), physical);
        return Send(inputs) == inputs.Count;
    }

    /// <summary>Re-sends held-back user keys in order, marked as replays.</summary>
    /// <param name="keys">The keys to replay.</param>
    /// <param name="physical">The Shift, Ctrl and Alt keys the user physically holds now; the batch ends in this state.</param>
    /// <returns>How many keys the system refused.</returns>
    public int Replay(IReadOnlyList<KeyStroke> keys, ModifierSides physical)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var (inputs, keyEnds) = BuildReplay(keys, CurrentModifiers(), physical);

        // SendInput stops at the first refused event; count the replayed keys that did not go out.
        var sent = (int)Send(inputs);
        return keyEnds.Count(end => end > sent);
    }

    /// <summary>The events that re-send a key press and release with the modifiers the user held.</summary>
    /// <param name="key">The original key.</param>
    /// <param name="current">The modifier state Windows has right now.</param>
    /// <param name="physical">The modifiers the user physically holds; the events end in this state.</param>
    internal static List<InputRecord> BuildResend(in KeyStroke key, ModifierSides current, ModifierSides physical)
    {
        var inputs = new List<InputRecord>(10);
        var state = current;
        if (key.VirtualKey != VkPacket)
        {
            SetModifiers(inputs, ref state, key.Modifiers);
        }

        AddEvent(inputs, key with { Flags = key.Flags & ~KeyStrokeAttributes.KeyUp }, InputMarkers.Own);
        AddEvent(inputs, key with { Flags = key.Flags | KeyStrokeAttributes.KeyUp }, InputMarkers.Own);
        RestoreTo(inputs, ref state, physical);
        return inputs;
    }

    /// <summary>The events that replay held keys, and where each key's events end in the list.</summary>
    /// <param name="keys">The held keys, in order.</param>
    /// <param name="current">The modifier state Windows has right now.</param>
    /// <param name="physical">The modifiers the user physically holds; the events end in this state.</param>
    internal static (List<InputRecord> Inputs, List<int> KeyEnds) BuildReplay(IReadOnlyList<KeyStroke> keys, ModifierSides current, ModifierSides physical)
    {
        var inputs = new List<InputRecord>(keys.Count + 8);
        var keyEnds = new List<int>(keys.Count);
        var state = current;
        foreach (var key in keys)
        {
            if (key.IsModifierKey)
            {
                state = Track(state, key);
            }
            else if (!key.IsKeyUp && key.VirtualKey != VkPacket)
            {
                SetModifiers(inputs, ref state, key.Modifiers);
            }

            AddEvent(inputs, key, InputMarkers.Replay);
            keyEnds.Add(inputs.Count);
        }

        // Modifiers stay as the last key needed them until here; now they match the keyboard again, so a
        // modifier is never left pressed and a key the user still holds keeps working.
        RestoreTo(inputs, ref state, physical);
        return (inputs, keyEnds);
    }

    /// <summary>
    /// Releases Alt or Win after a hotkey with a harmless unassigned key (VK 0xE8) in front of it. Without
    /// it, the app sees Alt or Win pressed and released alone (the hotkey's own key never reaches it), takes
    /// that as a tap, and opens its menu bar or the Start menu.
    /// </summary>
    /// <param name="vk">The Alt or Win key being released.</param>
    /// <param name="scan">Its scan code.</param>
    /// <param name="extended">Whether it is an extended key.</param>
    internal static void SendMaskedRelease(int vk, int scan, bool extended)
    {
        const int MaskKey = 0xE8;
        InputRecord[] inputs =
        [
            Raw(MaskKey, 0, up: false, extended: false, InputMarkers.Own),
            Raw(MaskKey, 0, up: true, extended: false, InputMarkers.Own),
            Raw(vk, scan, up: true, extended, InputMarkers.Own),
        ];
        Send(inputs);
    }

    /// <summary>The modifier state Windows has right now, per side.</summary>
    internal static ModifierSides CurrentModifiers()
    {
        var sides = ModifierSides.None;
        for (var i = 0; i < 6; i++)
        {
            if ((NativeMethods.GetAsyncKeyState(VkLeftShift + i) & 0x8000) != 0)
            {
                sides |= (ModifierSides)(1 << i);
            }
        }

        return sides;
    }

    private static ModifierSides Track(ModifierSides state, in KeyStroke key)
    {
        var side = key.VirtualKey switch
        {
            >= VkLeftShift and <= VkLeftShift + 5 => (ModifierSides)(1 << (key.VirtualKey - VkLeftShift)),
            0x10 => ModifierSides.LeftShift,
            0x11 => (key.Flags & KeyStrokeAttributes.Extended) != 0 ? ModifierSides.RightControl : ModifierSides.LeftControl,
            0x12 => (key.Flags & KeyStrokeAttributes.Extended) != 0 ? ModifierSides.RightAlt : ModifierSides.LeftAlt,
            _ => ModifierSides.None,
        };
        return key.IsKeyUp ? state & ~side : state | side;
    }

    /// <summary>Presses or releases Shift, Ctrl and Alt so that exactly the wanted ones are down (either side counts).</summary>
    private static void SetModifiers(List<InputRecord> inputs, ref ModifierSides state, KeyStrokeAttributes wanted)
    {
        (KeyStrokeAttributes Flag, ModifierSides Both, ModifierSides Left)[] groups =
        [
            (KeyStrokeAttributes.Shift, ModifierSides.LeftShift | ModifierSides.RightShift, ModifierSides.LeftShift),
            (KeyStrokeAttributes.Control, ModifierSides.LeftControl | ModifierSides.RightControl, ModifierSides.LeftControl),
            (KeyStrokeAttributes.Alt, ModifierSides.LeftAlt | ModifierSides.RightAlt, ModifierSides.LeftAlt),
        ];
        foreach (var (flag, both, left) in groups)
        {
            var wantDown = (wanted & flag) != 0;
            var isDown = (state & both) != 0;
            if (wantDown && !isDown)
            {
                inputs.Add(SideKey(left, up: false));
                state |= left;
            }
            else if (!wantDown && isDown)
            {
                // Release each side that is down, or the other side would keep the modifier active.
                foreach (var side in new[] { left, (ModifierSides)((int)left << 1) })
                {
                    if ((state & side) != 0)
                    {
                        inputs.Add(SideKey(side, up: true));
                    }
                }

                state &= ~both;
            }
        }
    }

    /// <summary>Presses or releases single sides until the state equals <paramref name="target"/>.</summary>
    private static void RestoreTo(List<InputRecord> inputs, ref ModifierSides state, ModifierSides target)
    {
        for (var i = 0; i < 6; i++)
        {
            var side = (ModifierSides)(1 << i);
            if ((state & side) != (target & side))
            {
                inputs.Add(SideKey(side, up: (target & side) == 0));
            }
        }

        state = target;
    }

    /// <summary>A press or release of one side's modifier key; right Ctrl and right Alt are extended keys.</summary>
    private static InputRecord SideKey(ModifierSides side, bool up)
    {
        var vk = (ushort)(VkLeftShift + System.Numerics.BitOperations.Log2((uint)side));
        return Key(vk, up, extended: side is ModifierSides.RightControl or ModifierSides.RightAlt);
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
