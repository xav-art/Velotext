// Velotext: tests for trigger validation (unusable, duplicate, blocked, hotkey problems).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Model;
using Velotext.Core.Validation;

namespace Velotext.Core.Tests.Validation;

/// <summary>Behaviour of <see cref="TriggerValidator"/>.</summary>
public sealed class TriggerValidatorTests
{
    [Fact]
    public void Clean_library_has_no_issues()
    {
        var library = Build.Library(Build.Phrase("a", Build.Auto("TWOX")), Build.Phrase("b", Build.Auto("TROIS"), Build.Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x31)));

        Assert.Empty(TriggerValidator.Validate(library));
    }

    [Fact]
    public void Unusable_triggers_are_errors()
    {
        var phrase = Build.Phrase("a", Build.Auto(string.Empty), Build.Auto(new string('x', 65)), Build.Auto("a\tb"));

        var issues = TriggerValidator.Validate(Build.Library(phrase));

        Assert.Equal([0, 1, 2], issues.Select(i => i.TriggerPosition));
        Assert.All(issues, i => Assert.Equal(IssueSeverity.Error, i.Severity));
    }

    [Fact]
    public void Exact_duplicate_is_an_error_on_the_later_trigger()
    {
        var first = Build.Phrase("First", Build.Auto("TWOX"));
        var second = Build.Phrase("Second", Build.Auto("TWOX"));

        var issue = Assert.Single(TriggerValidator.Validate(Build.Library(first, second)));

        Assert.Equal(second.Id, issue.PhraseId);
        Assert.Equal(IssueSeverity.Error, issue.Severity);
        Assert.Contains("First", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Case_insensitive_earlier_trigger_hides_later_completely()
    {
        var first = Build.Phrase("First", Build.Auto("twox", caseSensitive: false));
        var second = Build.Phrase("Second", Build.Auto("TWOX"));

        Assert.Equal(IssueSeverity.Error, Assert.Single(TriggerValidator.Validate(Build.Library(first, second))).Severity);
    }

    [Fact]
    public void Case_sensitive_earlier_trigger_hides_later_insensitive_partially()
    {
        var first = Build.Phrase("First", Build.Auto("TWOX"));
        var second = Build.Phrase("Second", Build.Auto("twox", caseSensitive: false));

        var issue = Assert.Single(TriggerValidator.Validate(Build.Library(first, second)));

        Assert.Equal(IssueSeverity.Warning, issue.Severity);
    }

    [Fact]
    public void Different_casing_with_both_case_sensitive_is_fine()
    {
        var library = Build.Library(Build.Phrase("a", Build.Auto("TWOX")), Build.Phrase("b", Build.Auto("twox")));

        Assert.Empty(TriggerValidator.Validate(library));
    }

    [Fact]
    public void Shorter_immediate_prefix_blocks_longer_trigger()
    {
        var blocker = Build.Phrase("Short", Build.Auto("BR"));
        var blocked = Build.Phrase("Long", Build.Auto("BRB"));

        var issue = Assert.Single(TriggerValidator.Validate(Build.Library(blocker, blocked)));

        Assert.Equal(blocked.Id, issue.PhraseId);
        Assert.Contains("\"BR\"", issue.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Immediate_trigger_with_same_text_blocks_delimiter_trigger()
    {
        var immediate = Build.Phrase("Now", Build.Auto("TWOX"));
        var delimited = Build.Phrase("Later", Build.Auto("TWOX", immediate: false));

        var issue = Assert.Single(TriggerValidator.Validate(Build.Library(delimited, immediate)));

        Assert.Equal(delimited.Id, issue.PhraseId);
    }

    [Fact]
    public void Delimiter_trigger_blocks_longer_trigger_containing_a_space()
    {
        var blocker = Build.Phrase("Best", Build.Auto("best", immediate: false));
        var blocked = Build.Phrase("Best regards", Build.Auto("best regards", immediate: false));

        var issue = Assert.Single(TriggerValidator.Validate(Build.Library(blocker, blocked)));

        Assert.Equal(blocked.Id, issue.PhraseId);
    }

    [Fact]
    public void Word_start_trigger_inside_another_trigger_does_not_block_it()
    {
        var inner = Build.Phrase("Inner", Build.Auto("UX"));
        var outer = Build.Phrase("Outer", Build.Auto("TWOX"));

        Assert.Empty(TriggerValidator.Validate(Build.Library(inner, outer)));
    }

    [Fact]
    public void Hotkey_problems_are_reported()
    {
        var invalid = Build.Phrase("Plain letter", Build.Hotkey(HotkeyModifiers.None, 0x41));
        var first = Build.Phrase("First", Build.Hotkey(HotkeyModifiers.Control, 0x70));
        var duplicate = Build.Phrase("Second", Build.Hotkey(HotkeyModifiers.Control, 0x70));

        var issues = TriggerValidator.Validate(Build.Library(invalid, first, duplicate));

        Assert.Equal(2, issues.Count);
        Assert.Equal(invalid.Id, issues[0].PhraseId);
        Assert.Equal(duplicate.Id, issues[1].PhraseId);
        Assert.Contains("Ctrl+F1", issues[1].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Phrases_in_folders_are_checked()
    {
        var a = Build.Phrase("A", Build.Auto("x1"));
        var b = Build.Phrase("B", Build.Auto("x1"));
        var library = Build.Library(new Folder { Id = Guid.NewGuid(), Name = "F", Children = [a, new Folder { Id = Guid.NewGuid(), Name = "G", Children = [b] }] });

        Assert.Single(TriggerValidator.Validate(library));
        Assert.Throws<ArgumentNullException>(() => TriggerValidator.Validate(null!));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("ok", false)]
    public void Describe_unusable_explains_problems(string? text, bool hasProblem) =>
        Assert.Equal(hasProblem, TriggerValidator.DescribeUnusable(text) is not null);

    [Theory]
    [InlineData("e", "Use at least 2 characters, so the trigger does not fire by accident.")]
    [InlineData(";;", "Add a letter or a digit, so the trigger does not fire in normal typing.")]
    [InlineData("  ", "Add a letter or a digit, so the trigger does not fire in normal typing.")]
    [InlineData(";x", null)]
    public void Short_or_letterless_triggers_are_refused(string text, string? expected) =>
        Assert.Equal(expected, TriggerValidator.DescribeUnusable(text));
}
