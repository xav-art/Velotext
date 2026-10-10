// Velotext: finds trigger problems (invalid, duplicate, blocked by another trigger, bad hotkeys).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;
using Velotext.Core.Matching;
using Velotext.Core.Model;

namespace Velotext.Core.Validation;

/// <summary>How serious a trigger issue is.</summary>
public enum IssueSeverity
{
    /// <summary>The trigger works but may surprise the user.</summary>
    Warning = 0,

    /// <summary>The trigger never fires.</summary>
    Error = 1,
}

/// <summary>How far one trigger hides another.</summary>
internal enum TriggerOverlap
{
    /// <summary>No typing matches both.</summary>
    None = 0,

    /// <summary>Some casings fire the earlier trigger instead.</summary>
    Partial = 1,

    /// <summary>The later trigger never fires.</summary>
    Full = 2,

    /// <summary>The earlier trigger wins at the start of a word; the later one still fires inside words.</summary>
    AtWordStart = 3,
}

/// <summary>A problem with one trigger of one phrase.</summary>
/// <param name="PhraseId">The phrase that owns the trigger.</param>
/// <param name="TriggerPosition">Zero-based position of the trigger in the phrase's trigger list.</param>
/// <param name="Severity">How serious it is.</param>
/// <param name="Message">Plain-English explanation shown under the trigger.</param>
public sealed record TriggerIssue(Guid PhraseId, int TriggerPosition, IssueSeverity Severity, string Message);

/// <summary>
/// Checks every trigger in a library. Blocked triggers are found by simulating typing each trigger
/// through the real matcher, so the check always agrees with what happens at run time.
/// </summary>
public static class TriggerValidator
{
    /// <summary>Validates all triggers in the library.</summary>
    /// <param name="library">The library to check.</param>
    public static IReadOnlyList<TriggerIssue> Validate(PhraseLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var issues = new List<TriggerIssue>();
        var phrases = library.EnumeratePhrases().ToList();
        var descriptions = phrases.ToDictionary(p => p.Id, p => p.Description);

        var autotexts = new List<(Phrase Phrase, int Position, AutotextTrigger Trigger)>();
        var hotkeys = new List<(Phrase Phrase, int Position, HotkeyTrigger Trigger)>();
        foreach (var phrase in phrases)
        {
            for (var position = 0; position < phrase.Triggers.Count; position++)
            {
                switch (phrase.Triggers[position])
                {
                    case AutotextTrigger autotext:
                        autotexts.Add((phrase, position, autotext));
                        break;
                    case HotkeyTrigger hotkey:
                        hotkeys.Add((phrase, position, hotkey));
                        break;
                }
            }
        }

        ValidateAutotexts(autotexts, descriptions, issues);
        ValidateHotkeys(hotkeys, descriptions, issues);
        return issues;
    }

    private static void ValidateAutotexts(
        List<(Phrase Phrase, int Position, AutotextTrigger Trigger)> autotexts,
        Dictionary<Guid, string> descriptions,
        List<TriggerIssue> issues)
    {
        var usable = new List<(Phrase Phrase, int Position, AutotextTrigger Trigger)>();
        foreach (var item in autotexts)
        {
            var problem = DescribeUnusable(item.Trigger.Text);
            if (problem is null)
            {
                usable.Add(item);
            }
            else
            {
                issues.Add(new TriggerIssue(item.Phrase.Id, item.Position, IssueSeverity.Error, problem));
            }
        }

        // Duplicates: the first one in library order wins.
        var duplicates = new HashSet<AutotextTrigger>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < usable.Count; i++)
        {
            for (var j = 0; j < i; j++)
            {
                var earlier = usable[j];
                var overlap = Overlap(earlier.Trigger, usable[i].Trigger);
                if (overlap == TriggerOverlap.None)
                {
                    continue;
                }

                // Only a full duplicate never fires; a partial one still fires sometimes, so it is also checked for blockers.
                if (overlap == TriggerOverlap.Full)
                {
                    duplicates.Add(usable[i].Trigger);
                }

                var owner = descriptions[earlier.Phrase.Id];
                issues.Add(overlap switch
                {
                    TriggerOverlap.Full => new TriggerIssue(usable[i].Phrase.Id, usable[i].Position, IssueSeverity.Error,
                        string.Create(CultureInfo.InvariantCulture, $"\"{owner}\" already uses \"{earlier.Trigger.Text}\". Change this trigger.")),
                    TriggerOverlap.AtWordStart => new TriggerIssue(usable[i].Phrase.Id, usable[i].Position, IssueSeverity.Warning,
                        string.Create(CultureInfo.InvariantCulture, $"At the start of a word, \"{earlier.Trigger.Text}\" inserts \"{owner}\". Change this trigger.")),
                    _ => new TriggerIssue(usable[i].Phrase.Id, usable[i].Position, IssueSeverity.Warning,
                        string.Create(CultureInfo.InvariantCulture, $"If you type \"{earlier.Trigger.Text}\" exactly, Velotext inserts \"{owner}\". Turn on Match case or change this trigger.")),
                });
                break;
            }
        }

        // Blocked: another trigger fires while this one is being typed.
        var index = TriggerIndex.Build(usable.Select(u => new AutotextEntry(u.Phrase.Id, u.Trigger)));
        foreach (var item in usable)
        {
            if (duplicates.Contains(item.Trigger))
            {
                continue;
            }

            var blocker = FindBlocker(item.Trigger, index);
            if (blocker is not null)
            {
                issues.Add(new TriggerIssue(
                    item.Phrase.Id,
                    item.Position,
                    IssueSeverity.Error,
                    string.Create(CultureInfo.InvariantCulture, $"This trigger does not fire. \"{blocker.Trigger.Text}\" inserts \"{descriptions[blocker.PhraseId]}\" before you finish typing it. Change one of the 2 triggers.")));
            }
        }
    }

    private static void ValidateHotkeys(
        List<(Phrase Phrase, int Position, HotkeyTrigger Trigger)> hotkeys,
        Dictionary<Guid, string> descriptions,
        List<TriggerIssue> issues)
    {
        for (var i = 0; i < hotkeys.Count; i++)
        {
            var item = hotkeys[i];
            var error = item.Trigger.Gesture.ValidationError;
            if (error is not null)
            {
                issues.Add(new TriggerIssue(item.Phrase.Id, item.Position, IssueSeverity.Error, error));
                continue;
            }

            for (var j = 0; j < i; j++)
            {
                if (hotkeys[j].Trigger.Gesture == item.Trigger.Gesture)
                {
                    issues.Add(new TriggerIssue(
                        item.Phrase.Id,
                        item.Position,
                        IssueSeverity.Error,
                        string.Create(CultureInfo.InvariantCulture, $"\"{descriptions[hotkeys[j].Phrase.Id]}\" already uses {item.Trigger.Gesture}. Choose a different hotkey.")));
                    break;
                }
            }
        }
    }

    /// <summary>Explains why a trigger text cannot be used, or returns <c>null</c> when it can.</summary>
    /// <param name="text">The trigger text.</param>
    public static string? DescribeUnusable(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "Type the text that should trigger this phrase.";
        }

        if (text.Length > AutotextTrigger.MaxLength)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Use {AutotextTrigger.MaxLength} characters or fewer.");
        }

        if (text.Any(char.IsControl))
        {
            return "Remove line breaks and tabs from the trigger.";
        }

        if (text.Length < 2)
        {
            return "Use at least 2 characters, so the trigger does not fire by accident.";
        }

        return text.Any(Matching.CharClass.IsWordChar) ? null : "Add a letter or a digit, so the trigger does not fire in normal typing.";
    }

    /// <summary>
    /// How much of <paramref name="later"/> is hidden by <paramref name="earlier"/> when both are the
    /// same kind and length. The earlier trigger wins every typing that matches both.
    /// </summary>
    private static TriggerOverlap Overlap(AutotextTrigger earlier, AutotextTrigger later)
    {
        if (earlier.Immediate != later.Immediate
            || !string.Equals(earlier.Text, later.Text, StringComparison.OrdinalIgnoreCase))
        {
            return TriggerOverlap.None;
        }

        TriggerOverlap casing;
        if (!earlier.CaseSensitive)
        {
            casing = TriggerOverlap.Full; // the earlier one matches every casing
        }
        else if (later.CaseSensitive)
        {
            // Both match only their exact casing.
            casing = string.Equals(earlier.Text, later.Text, StringComparison.Ordinal) ? TriggerOverlap.Full : TriggerOverlap.None;
        }
        else
        {
            casing = TriggerOverlap.Partial;
        }

        // An earlier trigger limited to the start of a word leaves the later one free inside words.
        return casing == TriggerOverlap.Full && earlier.WordStart && !later.WordStart ? TriggerOverlap.AtWordStart : casing;
    }

    private static AutotextEntry? FindBlocker(AutotextTrigger trigger, TriggerIndex index)
    {
        var matcher = new AutotextMatcher { Index = index };
        var text = trigger.Text;
        for (var i = 0; i < text.Length; i++)
        {
            var match = matcher.OnCharacter(text[i]);
            if (match is null || ReferenceEquals(match.Entry.Trigger, trigger))
            {
                continue;
            }

            var isLast = i == text.Length - 1;

            // On the last character, another immediate trigger of the same text is a duplicate (reported
            // separately). A delimiter trigger completed by this last character (for example "end" when the
            // trigger is "end.") always wins, and any immediate match pre-empts a delimiter trigger.
            if (!isLast || match.Delimiter is not null || !trigger.Immediate)
            {
                return match.Entry;
            }
        }

        return null;
    }
}
