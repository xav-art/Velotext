// Velotext: tests for building the trigger index from a library.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Matching;
using Velotext.Core.Model;

namespace Velotext.Core.Tests.Matching;

/// <summary>Behaviour of <see cref="TriggerIndex"/>.</summary>
public sealed class TriggerIndexTests
{
    [Fact]
    public void From_library_indexes_autotext_triggers_only_and_skips_unusable_ones()
    {
        var library = Build.Library(
            new Folder
            {
                Id = Guid.NewGuid(),
                Name = "F",
                Children =
                [
                    Build.Phrase("a", Build.Auto("one"), Build.Hotkey(HotkeyModifiers.Control, 0x41)),
                    Build.Phrase("b", Build.Auto(string.Empty), Build.Auto("bad\ttext"), Build.Auto(new string('x', 65))),
                ],
            },
            Build.Phrase("c", Build.Auto("two", immediate: false)));

        var index = TriggerIndex.FromLibrary(library);

        Assert.Equal(2, index.Count);
        Assert.Single(index.Candidates('e', immediate: true));
        Assert.Single(index.Candidates('O', immediate: false));
        Assert.Empty(index.Candidates('z', immediate: true));
    }

    [Fact]
    public void Candidates_are_longest_first_then_library_order()
    {
        var first = Build.Auto("ab");
        var second = Build.Auto("cab");
        var third = Build.Auto("xb");
        var index = Build.Index(first, second, third);

        var candidates = index.Candidates('B', immediate: true);

        Assert.Same(second, candidates[0].Trigger);
        Assert.Same(first, candidates[1].Trigger);
        Assert.Same(third, candidates[2].Trigger);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("ok", true)]
    [InlineData(";s", true)]
    [InlineData("tab\there", false)]
    [InlineData("e", false)]
    [InlineData(" ", false)]
    [InlineData(";;", false)]
    public void Is_usable_checks_text(string? text, bool expected) => Assert.Equal(expected, TriggerIndex.IsUsable(text));

    [Fact]
    public void Empty_index_has_no_candidates()
    {
        Assert.Equal(0, TriggerIndex.Empty.Count);
        Assert.Empty(TriggerIndex.Empty.Candidates('a', immediate: true));
    }

    [Fact]
    public void Arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => TriggerIndex.FromLibrary(null!));
        Assert.Throws<ArgumentNullException>(() => TriggerIndex.Build(null!));
    }
}
