// Velotext: shared helpers for core tests (builders, a manual clock, a recording sequencer sink).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Input;
using Velotext.Core.Matching;
using Velotext.Core.Model;

namespace Velotext.Core.Tests;

/// <summary>Builders for test data.</summary>
internal static class Build
{
    public static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    public static Phrase Phrase(string description, params Trigger[] triggers) => new()
    {
        Id = Guid.NewGuid(),
        Description = description,
        Content = Velotext.Core.RichText.RichDocument.FromPlainText(description + " content"),
        Triggers = triggers,
        CreatedUtc = Now,
        ModifiedUtc = Now,
    };

    public static AutotextTrigger Auto(string text, bool caseSensitive = true, bool immediate = true, bool wordStart = true) =>
        new() { Text = text, CaseSensitive = caseSensitive, Immediate = immediate, WordStart = wordStart };

    public static HotkeyTrigger Hotkey(HotkeyModifiers modifiers, int key) => new() { Gesture = new HotkeyGesture(modifiers, key) };

    public static PhraseLibrary Library(params LibraryNode[] items) => new() { Items = items };

    public static TriggerIndex Index(params AutotextTrigger[] triggers) =>
        TriggerIndex.Build(triggers.Select(t => new AutotextEntry(Guid.NewGuid(), t)));
}

/// <summary>A clock that only moves when the test says so.</summary>
internal sealed class ManualTime : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _ticks;

    public override DateTimeOffset GetUtcNow() => Build.Now + TimeSpan.FromTicks(_ticks);

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}

/// <summary>Translator that maps virtual keys to meanings with a fixed table, like a US layout without modifiers.</summary>
internal sealed class FakeTranslator : IKeyTranslator
{
    public const int Backspace = 0x08;
    public const int Left = 0x25;
    public const int Shift = 0x10;
    public const int Dead = 0xDE;

    public KeyMeaning Translate(in KeyStroke key) => key.VirtualKey switch
    {
        Backspace => KeyMeaning.Backspace,
        Left => KeyMeaning.Reset,
        Shift => KeyMeaning.Modifier,
        Dead => KeyMeaning.DeadKey,
        0xAD => KeyMeaning.Ignore,
        _ => KeyMeaning.ForText(((char)key.ScanCode).ToString()),
    };
}

/// <summary>Records what the sequencer asks for.</summary>
internal sealed class RecordingSink : IInputSequencerSink
{
    public List<ExpansionRequest> Expansions { get; } = [];

    public List<UndoRequest> Undos { get; } = [];

    public List<IReadOnlyList<KeyStroke>> Replays { get; } = [];

    public void ExpansionRequested(ExpansionRequest request) => Expansions.Add(request);

    public void UndoRequested(UndoRequest request) => Undos.Add(request);

    public void ReplayRequested(IReadOnlyList<KeyStroke> keys) => Replays.Add(keys);
}

/// <summary>Key event helpers. Text keys carry their character in the scan code field for <see cref="FakeTranslator"/>.</summary>
internal static class Keys
{
    public static KeyStroke Down(char c, KeyOrigin origin = KeyOrigin.Physical) => new(VkFor(c), c, KeyStrokeAttributes.None, origin);

    public static KeyStroke Up(char c, KeyOrigin origin = KeyOrigin.Physical) => new(VkFor(c), c, KeyStrokeAttributes.KeyUp, origin);

    public static KeyStroke DownVk(int vk, KeyOrigin origin = KeyOrigin.Physical) => new(vk, 0, KeyStrokeAttributes.None, origin);

    public static KeyStroke UpVk(int vk, KeyOrigin origin = KeyOrigin.Physical) => new(vk, 0, KeyStrokeAttributes.KeyUp, origin);

    // Distinct pseudo virtual keys per character so up/down pairing works in tests.
    private static int VkFor(char c) => 0x1000 + c;
}
