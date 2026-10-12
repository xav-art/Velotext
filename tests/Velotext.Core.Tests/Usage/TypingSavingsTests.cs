// Velotext: tests for counting the characters and typing time that phrases save.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Model;
using Velotext.Core.RichText;
using Velotext.Core.Usage;

namespace Velotext.Core.Tests.Usage;

/// <summary>Behaviour of <see cref="TypingSavings"/>: characters in a phrase, characters saved and time estimates.</summary>
public sealed class TypingSavingsTests
{
    [Theory]
    [InlineData("Thanks", 6)]
    [InlineData("Line one\nLine two", 17)]
    [InlineData("Merci \U0001F600", 7)]
    [InlineData("", 0)]
    public void Characters_count_line_breaks_and_emoji_as_one(string text, int expected) =>
        Assert.Equal(expected, TypingSavings.CharactersIn(RichDocument.FromPlainText(text)));

    [Fact]
    public void Saved_characters_subtract_the_trigger_and_never_go_below_zero()
    {
        var content = RichDocument.FromPlainText("Thank you for waiting");

        Assert.Equal(17, TypingSavings.CharactersSaved(content, 4));
        Assert.Equal(21, TypingSavings.CharactersSaved(content, -3));
        Assert.Equal(0, TypingSavings.CharactersSaved(RichDocument.FromPlainText("ok"), 4));
    }

    [Fact]
    public void Estimate_uses_the_first_typed_trigger_or_a_hotkey()
    {
        var typed = Build.Phrase("Thanks", Build.Auto(""), Build.Auto("/thx"), Build.Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54));
        var hotkeyOnly = Build.Phrase("Thanks", Build.Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x54));
        var none = Build.Phrase("Thanks");

        // Build.Phrase content is "<description> content": 14 characters.
        Assert.Equal(10, TypingSavings.EstimatedSavedPerUse(typed));
        Assert.Equal(13, TypingSavings.EstimatedSavedPerUse(hotkeyOnly));
        Assert.Equal(14, TypingSavings.EstimatedSavedPerUse(none));
    }

    [Fact]
    public void Typing_time_uses_200_characters_a_minute()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), TypingSavings.TypingTime(200));
        Assert.Equal(TimeSpan.FromMinutes(90), TypingSavings.TypingTime(18_000));
        Assert.Equal(TimeSpan.Zero, TypingSavings.TypingTime(-5));
        Assert.Equal(1, TypingSavings.HotkeyCharacters);
    }

    [Fact]
    public void Arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => TypingSavings.CharactersIn(null!));
        Assert.Throws<ArgumentNullException>(() => TypingSavings.EstimatedSavedPerUse(null!));
    }
}
