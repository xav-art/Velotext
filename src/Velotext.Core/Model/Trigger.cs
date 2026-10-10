// Velotext: trigger definitions (autotext and hotkey) that insert a phrase.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Velotext.Core.Model;

/// <summary>Something the user does that inserts a phrase.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AutotextTrigger), "autotext")]
[JsonDerivedType(typeof(HotkeyTrigger), "hotkey")]
public abstract record Trigger;

/// <summary>
/// Fires when the user types <see cref="Text"/> in any application.
/// All options default to on, which is the agreed product default.
/// </summary>
public sealed record AutotextTrigger : Trigger
{
    /// <summary>Longest allowed trigger text, in characters.</summary>
    public const int MaxLength = 64;

    /// <summary>The text to type.</summary>
    public required string Text { get; init; }

    /// <summary>When true, the typed text must match the case of <see cref="Text"/> exactly.</summary>
    public bool CaseSensitive { get; init; } = true;

    /// <summary>
    /// When true, the phrase is inserted as soon as the last character is typed.
    /// When false, it is inserted when a delimiter follows the trigger, and the delimiter is kept.
    /// </summary>
    public bool Immediate { get; init; } = true;

    /// <summary>When true, the trigger fires only at the start of a word.</summary>
    public bool WordStart { get; init; } = true;
}

/// <summary>Fires when the user presses a global key combination.</summary>
public sealed record HotkeyTrigger : Trigger
{
    /// <summary>The key combination.</summary>
    public required HotkeyGesture Gesture { get; init; }
}
