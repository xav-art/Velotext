// Velotext: works out how many characters and how much typing time a phrase saves.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;
using Velotext.Core.Model;
using Velotext.Core.RichText;

namespace Velotext.Core.Usage;

/// <summary>
/// Counts the characters that a phrase saves: the characters of its plain text (a line break and an emoji count as
/// one) minus the characters typed to trigger it. Time saved assumes an average typing speed.
/// </summary>
public static class TypingSavings
{
    /// <summary>Average typing speed used for time estimates: about 40 words a minute.</summary>
    public const int CharactersPerMinute = 200;

    /// <summary>Characters typed for a hotkey: one key press.</summary>
    public const int HotkeyCharacters = 1;

    /// <summary>Counts the characters of a phrase as plain text, with each line break and each emoji as one.</summary>
    /// <param name="content">The phrase content.</param>
    public static int CharactersIn(RichDocument content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = PlainTextRenderer.Render(content).Replace("\r\n", "\n", StringComparison.Ordinal);
        return new StringInfo(text).LengthInTextElements;
    }

    /// <summary>Returns the characters saved by one insertion, never below zero.</summary>
    /// <param name="content">The phrase content.</param>
    /// <param name="typedCharacters">Characters typed to trigger it (the trigger text, or one for a hotkey).</param>
    public static int CharactersSaved(RichDocument content, int typedCharacters) =>
        Math.Max(0, CharactersIn(content) - Math.Max(0, typedCharacters));

    /// <summary>
    /// Estimates the characters one use of a phrase saves, for uses stored before they were measured. It assumes the
    /// first typed text trigger, or a hotkey when the phrase has no typed text trigger.
    /// </summary>
    /// <param name="phrase">The phrase as it is today.</param>
    public static int EstimatedSavedPerUse(Phrase phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        var typed = phrase.Triggers.OfType<AutotextTrigger>().FirstOrDefault(t => t.Text.Length > 0)?.Text.Length
            ?? (phrase.Triggers.OfType<HotkeyTrigger>().Any() ? HotkeyCharacters : 0);
        return CharactersSaved(phrase.Content, typed);
    }

    /// <summary>Returns the typing time for a number of characters at <see cref="CharactersPerMinute"/>.</summary>
    /// <param name="characters">Number of characters.</param>
    public static TimeSpan TypingTime(long characters) =>
        TimeSpan.FromMinutes(Math.Max(0, characters) / (double)CharactersPerMinute);
}
