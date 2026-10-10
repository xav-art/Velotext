// Velotext: immutable lookup of autotext triggers, keyed by their last character for fast matching.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Collections.Frozen;
using Velotext.Core.Model;

namespace Velotext.Core.Matching;

/// <summary>An autotext trigger together with the phrase it inserts.</summary>
/// <param name="PhraseId">Id of the phrase to insert.</param>
/// <param name="Trigger">The trigger definition.</param>
public sealed record AutotextEntry(Guid PhraseId, AutotextTrigger Trigger);

/// <summary>
/// Immutable index of autotext triggers. Built off the keyboard thread and swapped in atomically, so
/// matching never takes a lock. Candidates are grouped by their last character (case-folded) and
/// sorted longest first, so the longest trigger wins when several end at the same position.
/// </summary>
public sealed class TriggerIndex
{
    private readonly FrozenDictionary<char, AutotextEntry[]> _immediate;
    private readonly FrozenDictionary<char, AutotextEntry[]> _delimited;

    private TriggerIndex(FrozenDictionary<char, AutotextEntry[]> immediate, FrozenDictionary<char, AutotextEntry[]> delimited, int count)
    {
        _immediate = immediate;
        _delimited = delimited;
        Count = count;
    }

    /// <summary>An index with no triggers.</summary>
    public static TriggerIndex Empty { get; } = Build([]);

    /// <summary>Number of usable triggers in the index.</summary>
    public int Count { get; }

    /// <summary>Builds an index from every autotext trigger in the library.</summary>
    /// <param name="library">The phrase library.</param>
    public static TriggerIndex FromLibrary(PhraseLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return Build(library.EnumeratePhrases()
            .SelectMany(phrase => phrase.Triggers.OfType<AutotextTrigger>().Select(trigger => new AutotextEntry(phrase.Id, trigger))));
    }

    /// <summary>
    /// Builds an index. Unusable triggers (empty, too long or containing control characters) are skipped.
    /// When two triggers are identical, the one that comes first wins.
    /// </summary>
    /// <param name="entries">Triggers in library order.</param>
    public static TriggerIndex Build(IEnumerable<AutotextEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var usable = entries.Where(e => IsUsable(e.Trigger.Text)).ToList();
        return new TriggerIndex(Group(usable, immediate: true), Group(usable, immediate: false), usable.Count);
    }

    /// <summary>Returns true when the text can be used as an autotext trigger.</summary>
    /// <param name="text">Trigger text.</param>
    public static bool IsUsable(string? text) =>
        !string.IsNullOrEmpty(text) && text.Length <= AutotextTrigger.MaxLength && !text.Any(char.IsControl);

    /// <summary>Returns candidate triggers whose last character matches <paramref name="lastChar"/>, longest first.</summary>
    /// <param name="lastChar">The character just before the match end.</param>
    /// <param name="immediate">Whether to look up immediate or delimiter triggers.</param>
    internal AutotextEntry[] Candidates(char lastChar, bool immediate) =>
        (immediate ? _immediate : _delimited).TryGetValue(char.ToUpperInvariant(lastChar), out var entries) ? entries : [];

    private static FrozenDictionary<char, AutotextEntry[]> Group(List<AutotextEntry> entries, bool immediate) =>
        entries
            .Where(e => e.Trigger.Immediate == immediate)
            .Select((entry, order) => (entry, order))
            .GroupBy(x => char.ToUpperInvariant(x.entry.Trigger.Text[^1]))
            .ToFrozenDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.entry.Trigger.Text.Length).ThenBy(x => x.order).Select(x => x.entry).ToArray());
}
