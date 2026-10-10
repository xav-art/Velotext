// Velotext: platform-neutral description of one keyboard event and what it means for matching.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

namespace Velotext.Core.Input;

/// <summary>Who produced a keyboard event.</summary>
public enum KeyOrigin
{
    /// <summary>The user (or another program) typed it.</summary>
    Physical = 0,

    /// <summary>Velotext re-sent it after holding it back during an expansion.</summary>
    Replay = 1,

    /// <summary>Velotext generated it to perform an expansion (backspaces, paste, typing).</summary>
    Own = 2,
}

/// <summary>Attributes of a keyboard event.</summary>
[Flags]
public enum KeyStrokeAttributes
{
    /// <summary>Key pressed.</summary>
    None = 0,

    /// <summary>Key released.</summary>
    KeyUp = 1,

    /// <summary>Extended key (for example right Ctrl, arrow keys on the main block).</summary>
    Extended = 2,

    /// <summary>Shift was held when the user pressed the key.</summary>
    Shift = 4,

    /// <summary>Ctrl was held when the user pressed the key (with Alt, this is AltGr).</summary>
    Control = 8,

    /// <summary>Alt was held when the user pressed the key (with Ctrl, this is AltGr).</summary>
    Alt = 16,
}

/// <summary>One keyboard event as seen by the low-level hook.</summary>
/// <param name="VirtualKey">Windows virtual-key code.</param>
/// <param name="ScanCode">Hardware scan code.</param>
/// <param name="Flags">Up/down and extended attributes.</param>
/// <param name="Origin">Who produced the event.</param>
public readonly record struct KeyStroke(int VirtualKey, int ScanCode, KeyStrokeAttributes Flags, KeyOrigin Origin)
{
    /// <summary>True for a key release.</summary>
    public bool IsKeyUp => (Flags & KeyStrokeAttributes.KeyUp) != 0;

    /// <summary>True for Shift, Ctrl, Alt and Windows keys (either side).</summary>
    public bool IsModifierKey => VirtualKey is 0x10 or 0x11 or 0x12 or (>= 0xA0 and <= 0xA5) or 0x5B or 0x5C;

    /// <summary>The modifiers the user held when pressing the key (Shift, Control, Alt).</summary>
    public KeyStrokeAttributes Modifiers => Flags & (KeyStrokeAttributes.Shift | KeyStrokeAttributes.Control | KeyStrokeAttributes.Alt);

    /// <summary>Returns a copy marked as replayed by Velotext.</summary>
    public KeyStroke AsReplay() => this with { Origin = KeyOrigin.Replay };
}

/// <summary>What a key press means for trigger matching.</summary>
public enum KeyMeaningKind
{
    /// <summary>No effect on the typed text (volume keys, key releases and so on).</summary>
    Ignore = 0,

    /// <summary>Shift, Ctrl, Alt, Win or a lock key on its own.</summary>
    Modifier = 1,

    /// <summary>Produces text; see <see cref="KeyMeaning.Text"/>.</summary>
    Text = 2,

    /// <summary>Plain Backspace: removes one character.</summary>
    Backspace = 3,

    /// <summary>Moves the caret or runs a command, so the typed context is no longer known.</summary>
    Reset = 4,

    /// <summary>A dead key waiting for the next key to compose an accented character.</summary>
    DeadKey = 5,
}

/// <summary>The meaning of a key press for trigger matching.</summary>
/// <param name="Kind">The kind of effect.</param>
/// <param name="Text">The produced text for <see cref="KeyMeaningKind.Text"/>; one or two UTF-16 units.</param>
/// <param name="AfterDeadKey">True when the key completed a dead key (^ then e); the application still holds the dead key until it sees this key.</param>
public readonly record struct KeyMeaning(KeyMeaningKind Kind, string? Text = null, bool AfterDeadKey = false)
{
    /// <summary>No effect.</summary>
    public static KeyMeaning Ignore { get; } = new(KeyMeaningKind.Ignore);

    /// <summary>A modifier key.</summary>
    public static KeyMeaning Modifier { get; } = new(KeyMeaningKind.Modifier);

    /// <summary>Plain Backspace.</summary>
    public static KeyMeaning Backspace { get; } = new(KeyMeaningKind.Backspace);

    /// <summary>Context lost.</summary>
    public static KeyMeaning Reset { get; } = new(KeyMeaningKind.Reset);

    /// <summary>Dead key.</summary>
    public static KeyMeaning DeadKey { get; } = new(KeyMeaningKind.DeadKey);

    /// <summary>Creates a text meaning.</summary>
    /// <param name="text">The produced text.</param>
    public static KeyMeaning ForText(string text) => new(KeyMeaningKind.Text, text);
}

/// <summary>Turns a key press into its meaning, using the active keyboard layout.</summary>
public interface IKeyTranslator
{
    /// <summary>Translates a key press (never called for key releases).</summary>
    /// <param name="key">The key press.</param>
    KeyMeaning Translate(in KeyStroke key);
}
