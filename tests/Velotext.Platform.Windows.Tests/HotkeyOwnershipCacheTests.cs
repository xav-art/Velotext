// Velotext: tests for the cache that tells whether another program owns a key combination as a hotkey.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Platform.Windows.Input;

namespace Velotext.Platform.Windows.Tests;

/// <summary>Keys owned as hotkeys elsewhere are recognised, cached for a minute and checked again after that.</summary>
public sealed class HotkeyOwnershipCacheTests
{
    private sealed class SteppedTime : TimeProvider
    {
        public long Ticks { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Ticks;
    }

    [Fact]
    public void Answers_are_cached_per_combination_and_refreshed_after_a_minute()
    {
        var calls = new List<(uint Modifiers, uint Vk)>();
        var claimed = true;
        var time = new SteppedTime();
        var cache = new HotkeyOwnershipCache((modifiers, vk) =>
        {
            calls.Add((modifiers, vk));
            return claimed;
        }, time);

        Assert.True(cache.IsClaimed(0xBC, shift: true, ctrl: false, alt: false));
        Assert.True(cache.IsClaimed(0xBC, shift: true, ctrl: false, alt: false));
        Assert.Equal([(4u, 0xBCu)], calls);

        // Another combination of the same key is a separate question.
        claimed = false;
        Assert.False(cache.IsClaimed(0xBC, shift: false, ctrl: true, alt: true));
        Assert.Equal((3u, 0xBCu), calls[^1]);

        // After a minute the first answer is checked again: the program may have quit.
        time.Ticks += TimeSpan.FromMinutes(1).Ticks + 1;
        Assert.False(cache.IsClaimed(0xBC, shift: true, ctrl: false, alt: false));
        Assert.Equal(3, calls.Count);
    }

    [Fact]
    public void An_unknown_answer_counts_as_typed_text()
    {
        var cache = new HotkeyOwnershipCache((_, _) => null, new SteppedTime());

        Assert.False(cache.IsClaimed(0x41, shift: false, ctrl: false, alt: false));
    }
}
