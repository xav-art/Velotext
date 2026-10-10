// Velotext: turns low-level key events into text using the keyboard layout of the focused application.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Input;
using Velotext.Platform.Windows.Native;

namespace Velotext.Platform.Windows.Input;

/// <summary>
/// Translates key presses into text with the focused application's keyboard layout. It calls
/// <c>ToUnicodeEx</c> with the "do not change keyboard state" flag, so dead keys (^, `, ¨ on French
/// and German layouts) keep working in the target application. Shortcuts (Ctrl, Alt or Win with a
/// key) and navigation keys report <see cref="KeyMeaningKind.Reset"/>; AltGr characters such as @ and €
/// are text. Because the dead key is not stored in the keyboard state, the translator composes it with
/// the next character itself (^ then e gives ê), as the target application does. Runs on the keyboard
/// hook thread.
/// </summary>
internal sealed unsafe class KeyboardTranslator : IKeyTranslator
{
    private const int VkBack = 0x08;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkCapital = 0x14;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkPacket = 0xE7;
    private const uint DoNotChangeKeyboardState = 0x4;

    private static readonly string[] s_ascii = [.. Enumerable.Range(0, 128).Select(i => ((char)i).ToString())];

    private readonly byte[] _state = new byte[256];
    private char _pendingDeadKey;

    /// <inheritdoc />
    public KeyMeaning Translate(in KeyStroke key)
    {
        var vk = key.VirtualKey;

        // Text sent by on-screen keyboards and other tools arrives as VK_PACKET with the character in the scan code.
        if (vk == VkPacket)
        {
            var packet = (char)key.ScanCode;
            return KeyMeaning.ForText(packet < 128 ? s_ascii[packet] : packet.ToString());
        }

        if (IsModifierOrLock(vk))
        {
            return KeyMeaning.Modifier;
        }

        var meaning = TranslateKey(key, vk);
        if (meaning.Kind == KeyMeaningKind.DeadKey)
        {
            return meaning;
        }

        var dead = _pendingDeadKey;
        _pendingDeadKey = '\0';
        return dead != '\0' && meaning is { Kind: KeyMeaningKind.Text, Text: { } text } ? KeyMeaning.ForText(Compose(dead, text)) : meaning;
    }

    /// <summary>
    /// Combines a dead key with the next typed text the way Windows does: a composed letter when one
    /// exists (^ and e give ê), the accent alone after a space, and both characters otherwise.
    /// </summary>
    /// <param name="deadKey">The spacing form of the dead key, as reported by the layout.</param>
    /// <param name="text">The text of the next key.</param>
    internal static string Compose(char deadKey, string text)
    {
        if (text == " ")
        {
            return deadKey.ToString();
        }

        var combining = CombiningMark(deadKey);
        if (text.Length == 1 && combining != '\0')
        {
            var composed = string.Concat(text, combining.ToString()).Normalize(System.Text.NormalizationForm.FormC);
            if (composed.Length == 1)
            {
                return composed;
            }
        }

        return deadKey + text;
    }

    private static char CombiningMark(char deadKey) => deadKey switch
    {
        '`' => '\u0300',
        '\'' or '\u00B4' => '\u0301',
        '^' or '\u02C6' => '\u0302',
        '~' or '\u02DC' => '\u0303',
        '\u00AF' => '\u0304',
        '\u02D8' => '\u0306',
        '\u02D9' => '\u0307',
        '"' or '\u00A8' => '\u0308',
        '\u00B0' or '\u02DA' => '\u030A',
        '\u02DD' => '\u030B',
        '\u02C7' => '\u030C',
        '\u00B8' => '\u0327',
        '\u02DB' => '\u0328',
        _ => '\0',
    };

    private KeyMeaning TranslateKey(in KeyStroke key, int vk)
    {

        var ctrl = IsDown(VkControl);
        var alt = IsDown(VkMenu);
        var win = IsDown(VkLWin) || IsDown(VkRWin);

        if (vk == VkBack)
        {
            return ctrl || alt || win ? KeyMeaning.Reset : KeyMeaning.Backspace;
        }

        var category = Classify(vk);
        if (category != KeyMeaningKind.Text)
        {
            return category == KeyMeaningKind.Ignore ? KeyMeaning.Ignore : KeyMeaning.Reset;
        }

        // Ctrl or Alt alone (or Win) means a shortcut. Ctrl and Alt together may be AltGr, which types text.
        if (win || ctrl != alt)
        {
            return KeyMeaning.Reset;
        }

        Array.Clear(_state);
        if (IsDown(VkShift))
        {
            _state[VkShift] = _state[0xA0] = 0x80;
        }

        if (ctrl)
        {
            _state[VkControl] = _state[0xA2] = 0x80;
        }

        if (alt)
        {
            _state[VkMenu] = _state[0xA4] = 0x80;
        }

        if ((NativeMethods.GetKeyState(VkCapital) & 0x1) != 0)
        {
            _state[VkCapital] = 0x01;
        }

        var foreground = NativeMethods.GetForegroundWindow();
        var layout = NativeMethods.GetKeyboardLayout(foreground == 0 ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _));
        var buffer = stackalloc char[8];
        int count;
        fixed (byte* state = _state)
        {
            count = NativeMethods.ToUnicodeEx((uint)vk, (uint)key.ScanCode, state, buffer, 8, DoNotChangeKeyboardState, layout);
        }

        if (count < 0)
        {
            _pendingDeadKey = buffer[0];
            return KeyMeaning.DeadKey;
        }

        if (count == 0)
        {
            return ctrl ? KeyMeaning.Reset : KeyMeaning.Ignore;
        }

        for (var i = 0; i < count; i++)
        {
            if (buffer[i] < ' ' && buffer[i] != '\r' && buffer[i] != '\t')
            {
                return KeyMeaning.Reset;
            }
        }

        // Common characters reuse a cached string, so typing does not leave a trail of strings in memory.
        return KeyMeaning.ForText(count == 1 && buffer[0] < 128 ? s_ascii[buffer[0]] : new string(buffer, 0, count));
    }

    /// <summary>Returns whether a key moves the caret, runs a command or does nothing to the text.</summary>
    /// <param name="vk">Virtual-key code.</param>
    internal static KeyMeaningKind Classify(int vk) => vk switch
    {
        0x1B => KeyMeaningKind.Reset,                     // Esc
        >= 0x21 and <= 0x28 => KeyMeaningKind.Reset,      // Page Up/Down, End, Home, arrows
        0x2D or 0x2E => KeyMeaningKind.Reset,             // Insert, Delete
        0x5D => KeyMeaningKind.Reset,                     // context menu key
        >= 0x70 and <= 0x87 => KeyMeaningKind.Reset,      // F1 to F24
        >= 0xA6 and <= 0xB7 => KeyMeaningKind.Ignore,     // browser, volume and media keys
        0x2C or 0x13 => KeyMeaningKind.Ignore,            // Print Screen, Pause
        _ => KeyMeaningKind.Text,
    };

    private static bool IsModifierOrLock(int vk) =>
        vk is VkShift or VkControl or VkMenu or VkCapital or 0x90 or 0x91 or VkLWin or VkRWin or (>= 0xA0 and <= 0xA5);

    private static bool IsDown(int vk) => (NativeMethods.GetAsyncKeyState(vk) & 0x8000) != 0;
}
