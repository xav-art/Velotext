// Velotext: tests for how re-sent and replayed keys get back the modifiers the user held, and for dead-key composition.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Input;
using Velotext.Platform.Windows.Input;
using Velotext.Platform.Windows.Native;

namespace Velotext.Platform.Windows.Tests;

/// <summary>The input events Velotext builds for re-sent and replayed keys, without sending them.</summary>
public sealed class InputInjectorTests
{
    private const int VkA = 0x41;
    private const int VkReturn = 0x0D;

    private static string Describe(InputRecord input)
    {
        var key = input.Data.Keyboard;
        var up = (key.Flags & NativeMethods.KeyeventfKeyUp) != 0 ? "up" : "down";
        var who = key.ExtraInfo == InputMarkers.Replay ? "replay" : "own";
        return (key.Flags & NativeMethods.KeyeventfUnicode) != 0
            ? $"unicode {(char)key.ScanCode} {up} {who}"
            : $"{key.VirtualKey:X2} {up} {who}";
    }

    private static KeyStroke Stroke(int vk, KeyStrokeAttributes flags = KeyStrokeAttributes.None, KeyOrigin origin = KeyOrigin.Replay) => new(vk, 0x1C, flags, origin);

    [Fact]
    public void Shift_enter_is_resent_with_shift_even_after_shift_was_released()
    {
        var inputs = InputInjector.BuildResend(Stroke(VkReturn, KeyStrokeAttributes.Shift), KeyStrokeAttributes.None);

        Assert.Equal(["10 down own", "0D down own", "10 up own", "0D up own"], inputs.Select(Describe));
    }

    [Fact]
    public void Resend_without_a_modifier_change_is_just_the_key()
    {
        var inputs = InputInjector.BuildResend(Stroke(VkA), KeyStrokeAttributes.None);

        Assert.Equal(["41 down own", "41 up own"], inputs.Select(Describe));
    }

    [Fact]
    public void Replay_restores_shift_and_altgr_and_puts_the_current_state_back()
    {
        KeyStroke[] keys =
        [
            Stroke(VkA, KeyStrokeAttributes.Shift),
            Stroke(VkA, KeyStrokeAttributes.KeyUp),
            Stroke(0x32, KeyStrokeAttributes.Control | KeyStrokeAttributes.Alt),
        ];

        var (inputs, ends) = InputInjector.BuildReplay(keys, KeyStrokeAttributes.None);

        Assert.Equal(
            [
                "10 down own", "41 down replay", "10 up own",
                "41 up replay",
                "11 down own", "12 down own", "32 down replay", "11 up own", "12 up own",
            ],
            inputs.Select(Describe));
        Assert.Equal([3, 4, 9], ends);
    }

    [Fact]
    public void Replay_lifts_a_held_shift_for_a_key_typed_without_it()
    {
        var (inputs, _) = InputInjector.BuildReplay([Stroke(VkA)], KeyStrokeAttributes.Shift);

        Assert.Equal(["10 up own", "41 down replay", "10 down own"], inputs.Select(Describe));
    }

    [Fact]
    public void Replayed_modifier_presses_count_for_the_following_keys()
    {
        var (inputs, _) = InputInjector.BuildReplay(
            [Stroke(0xA0), Stroke(VkA, KeyStrokeAttributes.Shift), Stroke(0xA0, KeyStrokeAttributes.KeyUp)],
            KeyStrokeAttributes.None);

        Assert.Equal(["A0 down replay", "41 down replay", "A0 up replay"], inputs.Select(Describe));
    }

    [Fact]
    public void Packet_keys_are_replayed_as_unicode()
    {
        var packet = new KeyStroke(InputInjector.VkPacket, 'é', KeyStrokeAttributes.Shift, KeyOrigin.Replay);

        var (inputs, _) = InputInjector.BuildReplay([packet, packet with { Flags = KeyStrokeAttributes.KeyUp }], KeyStrokeAttributes.None);

        Assert.Equal(["unicode é down replay", "unicode é up replay"], inputs.Select(Describe));
    }

    [Theory]
    [InlineData('^', "e", "\u00EA")]
    [InlineData('\u00B4', "e", "\u00E9")]
    [InlineData('`', "a", "\u00E0")]
    [InlineData('\u00A8', "u", "\u00FC")]
    [InlineData('~', "n", "\u00F1")]
    [InlineData('\u02C7', "c", "\u010D")]
    [InlineData('^', " ", "^")]
    [InlineData('^', "x", "^x")]
    [InlineData('#', "a", "#a")]
    [InlineData('^', "ab", "^ab")]
    public void Dead_keys_compose_like_windows(char deadKey, string next, string expected) =>
        Assert.Equal(expected, KeyboardTranslator.Compose(deadKey, next));
}
