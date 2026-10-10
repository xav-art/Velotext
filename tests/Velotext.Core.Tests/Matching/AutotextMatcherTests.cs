// Velotext: tests for the autotext matcher (case, immediate vs delimiter, word start, backspace, reset, seeding).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Matching;

namespace Velotext.Core.Tests.Matching;

/// <summary>Behaviour of <see cref="AutotextMatcher"/> on typed character streams.</summary>
public sealed class AutotextMatcherTests
{
    private static AutotextMatch? TypeAll(AutotextMatcher matcher, string text)
    {
        AutotextMatch? last = null;
        foreach (var c in text)
        {
            last = matcher.OnCharacter(c);
            if (last is not null)
            {
                return last;
            }
        }

        return last;
    }

    [Fact]
    public void Immediate_trigger_fires_on_last_character_and_deletes_one_less()
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("TWOX")) };

        var match = TypeAll(matcher, "TWOX");

        Assert.NotNull(match);
        Assert.Equal("TWOX", match.TypedText);
        Assert.Equal(3, match.CharactersToDelete);
        Assert.Null(match.Delimiter);
    }

    [Fact]
    public void Case_sensitive_trigger_ignores_other_casing()
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("TWOX")) };

        Assert.Null(TypeAll(matcher, "twox"));
        Assert.Null(TypeAll(matcher, " Twox"));
    }

    [Fact]
    public void Case_insensitive_trigger_matches_any_casing_and_reports_typed_text()
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("twox", caseSensitive: false)) };

        var match = TypeAll(matcher, "TwOx");

        Assert.NotNull(match);
        Assert.Equal("TwOx", match.TypedText);
    }

    [Fact]
    public void Word_start_blocks_trigger_inside_a_word()
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("TWOX")) };

        Assert.Null(TypeAll(matcher, "ATWOX"));
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r")]
    [InlineData("(")]
    [InlineData(",")]
    public void Word_start_allows_trigger_after_a_delimiter(string delimiter)
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("TWOX")) };

        Assert.NotNull(TypeAll(matcher, "abc" + delimiter + "TWOX"));
    }

    [Fact]
    public void Word_start_off_allows_trigger_inside_a_word()
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("TWOX", wordStart: false)) };

        Assert.NotNull(TypeAll(matcher, "ATWOX"));
    }

    [Fact]
    public void Trigger_starting_with_a_delimiter_fires_after_a_word()
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto(";sig")) };

        Assert.NotNull(TypeAll(matcher, "hello;sig"));
    }

    [Fact]
    public void Delimiter_trigger_waits_for_delimiter_and_reports_it()
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("brb", immediate: false)) };

        Assert.Null(TypeAll(matcher, "brb"));
        var match = matcher.OnCharacter(' ');

        Assert.NotNull(match);
        Assert.Equal(' ', match.Delimiter);
        Assert.Equal(3, match.CharactersToDelete);
        Assert.Equal("brb", match.TypedText);
    }

    [Fact]
    public void Delimiter_trigger_does_not_fire_when_word_continues()
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("brb", immediate: false)) };

        Assert.Null(TypeAll(matcher, "brbx "));
    }

    [Fact]
    public void Longest_trigger_wins_when_several_end_together()
    {
        var shortTrigger = Build.Auto("UX", wordStart: false);
        var longTrigger = Build.Auto("TWOX");
        var matcher = new AutotextMatcher { Index = Build.Index(shortTrigger, longTrigger) };

        var match = TypeAll(matcher, "TWOX");

        Assert.Same(longTrigger, match!.Entry.Trigger);
    }

    [Fact]
    public void Backspace_removes_typed_character()
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("TWOX")) };

        Assert.Null(TypeAll(matcher, "TWOZ"));
        matcher.OnBackspace();

        Assert.NotNull(matcher.OnCharacter('X'));
    }

    [Fact]
    public void Backspace_on_empty_buffer_makes_context_unknown_so_word_start_passes()
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("TWOX")) };
        matcher.OnBackspace();

        Assert.NotNull(TypeAll(matcher, "TWOX"));
    }

    [Fact]
    public void Reset_forgets_partial_trigger()
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("TWOX")) };

        TypeAll(matcher, "DE");
        matcher.Reset();

        Assert.Null(TypeAll(matcher, "UX"));
    }

    [Fact]
    public void Seed_restores_context_without_refiring_delimiter_trigger_once()
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("brb", immediate: false)) };

        matcher.Seed("brb");

        Assert.Null(matcher.OnCharacter(' '));
        Assert.NotNull(TypeAll(matcher, "brb "));
    }

    [Fact]
    public void Seed_suppression_ends_after_backspace_below_seed()
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("brb", immediate: false)) };

        matcher.Seed("brb");
        matcher.OnBackspace();

        Assert.NotNull(TypeAll(matcher, "b "));
    }

    [Fact]
    public void Buffer_overflow_keeps_matching_and_tracks_boundary()
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("TWOX")) };

        TypeAll(matcher, new string('a', 500) + " ");
        Assert.NotNull(TypeAll(matcher, "TWOX"));

        TypeAll(matcher, new string('b', 300));
        Assert.Null(TypeAll(matcher, "TWOX"));
    }

    [Theory]
    [InlineData(' ', true)]
    [InlineData('a', false)]
    public void Overflow_remembers_whether_the_dropped_text_ended_with_a_delimiter(char lastDropped, bool expectFire)
    {
        var matcher = new AutotextMatcher(capacity: 128) { Index = Build.Index(Build.Auto("TWOX")) };

        // Fill the buffer so the character at position 63 is the last one dropped by the trim.
        TypeAll(matcher, new string('a', 63) + lastDropped + new string('b', 64));
        TypeAll(matcher, "c");

        // Delete everything still buffered; the caret now sits right after the dropped text.
        for (var i = 0; i < 65; i++)
        {
            matcher.OnBackspace();
        }

        Assert.Equal(expectFire, TypeAll(matcher, "TWOX") is not null);
    }

    [Fact]
    public void Index_can_be_swapped()
    {
        var matcher = new AutotextMatcher();
        Assert.Null(TypeAll(matcher, "TWOX"));

        matcher.Reset();
        matcher.Index = Build.Index(Build.Auto("TWOX"));

        Assert.NotNull(TypeAll(matcher, "TWOX"));
        Assert.Throws<ArgumentNullException>(() => matcher.Index = null!);
    }

    [Fact]
    public void Capacity_must_hold_two_longest_triggers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AutotextMatcher(capacity: 10));
    }

    [Fact]
    public void Matcher_never_keeps_more_than_capacity()
    {
        var matcher = new AutotextMatcher(capacity: 128);

        TypeAll(matcher, new string('x', 1000));

        Assert.True(matcher.BufferText.Length <= 128);
    }

    [Fact]
    public void Seed_rejects_null() => Assert.Throws<ArgumentNullException>(() => new AutotextMatcher().Seed(null!));

    [Theory]
    [InlineData('a', true)]
    [InlineData('9', true)]
    [InlineData('_', true)]
    [InlineData((char)0x00E9, true)]
    [InlineData((char)0x0301, true)]
    [InlineData(' ', false)]
    [InlineData('-', false)]
    [InlineData('@', false)]
    public void Char_class_separates_word_characters(char c, bool isWord)
    {
        Assert.Equal(isWord, CharClass.IsWordChar(c));
        Assert.Equal(!isWord, CharClass.IsDelimiter(c));
    }

    [Theory]
    [InlineData('\r')]
    [InlineData('\t')]
    public void Enter_and_tab_complete_delimiter_triggers_then_forget_the_field(char key)
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("brb", immediate: false), Build.Auto("pass")) };

        Assert.NotNull(TypeAll(matcher, "brb" + key));
        matcher.Reset();
        Assert.Null(TypeAll(matcher, "pa" + key));
        Assert.Equal(string.Empty, matcher.BufferText);

        // The text before Enter or Tab is gone, so it cannot complete a trigger afterwards.
        Assert.Null(TypeAll(matcher, "ss"));
    }

    [Fact]
    public void Fed_characters_keep_the_buffer_in_step_without_matching()
    {
        var matcher = new AutotextMatcher { Index = Build.Index(Build.Auto("ab"), Build.Auto("brb", immediate: false)) };

        // "ab" is a trigger, but fed characters never match; the buffer still has them.
        matcher.Feed('a');
        matcher.Feed('b');
        Assert.Equal("ab", matcher.BufferText);
        Assert.NotNull(TypeAll(matcher, " brb "));

        matcher.Feed('\r');
        Assert.Equal(string.Empty, matcher.BufferText);
    }
}
