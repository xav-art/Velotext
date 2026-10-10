// Velotext: tests for modifier tracking (specific and generic keys) and the Alt or Win release mask after hotkeys.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Collections.Frozen;
using Velotext.Core.Input;
using Velotext.Core.Model;
using Velotext.Platform.Windows.Input;

namespace Velotext.Platform.Windows.Tests;

/// <summary>Behaviour of <see cref="PhysicalKeyboardState"/>: what the user holds, and when a release is masked.</summary>
public sealed class PhysicalKeyboardStateTests
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkF11 = 0x7A;

    private static readonly FrozenSet<HotkeyGesture> s_hotkeys = new[]
    {
        new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, VkF11),
        new HotkeyGesture(HotkeyModifiers.Windows, 0x4E),
        new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x51),
    }.ToFrozenSet();

    private readonly PhysicalKeyboardState _state = new();

    /// <summary>Feeds one event and returns what the hook would do with it.</summary>
    private ReleaseMask Key(int vk, bool up, int scan = 0, bool extended = false)
    {
        _state.Apply(vk, scan, extended, up);
        return _state.Decide(vk, scan, extended, up, s_hotkeys);
    }

    [Theory]
    [InlineData(VkShift, 0x2A, false, ModifierSides.LeftShift)]
    [InlineData(VkShift, 0x36, false, ModifierSides.RightShift)]
    [InlineData(VkControl, 0x1D, false, ModifierSides.LeftControl)]
    [InlineData(VkControl, 0x1D, true, ModifierSides.RightControl)]
    [InlineData(VkMenu, 0x38, false, ModifierSides.LeftAlt)]
    [InlineData(VkMenu, 0x38, true, ModifierSides.RightAlt)]
    [InlineData(0xA3, 0x1D, true, ModifierSides.RightControl)]
    [InlineData(0x41, 0x1E, false, ModifierSides.None)]
    public void Generic_and_specific_modifier_keys_map_to_a_side(int vk, int scan, bool extended, ModifierSides expected) =>
        Assert.Equal(expected, PhysicalKeyboardState.SideOf(vk, scan, extended));

    [Fact]
    public void Generic_modifiers_from_software_are_tracked_like_keyboard_ones()
    {
        Key(VkControl, up: false);
        Key(VkMenu, up: false);
        Key(VkShift, up: false);

        Assert.Equal(KeyStrokeAttributes.Shift | KeyStrokeAttributes.Control | KeyStrokeAttributes.Alt, _state.Flags);

        Key(VkShift, up: true);
        Key(VkMenu, up: true);
        Key(VkControl, up: true);
        Assert.Equal(KeyStrokeAttributes.None, _state.Flags);
    }

    [Fact]
    public void Alt_release_after_an_alt_hotkey_waits_for_ctrl_and_is_sent_once_even_with_generic_keys()
    {
        // A whole chord sent at once by software: Ctrl, Alt, Shift down, F11 down and up, then the releases.
        Assert.Equal(ReleaseMask.None, Key(VkControl, up: false));
        Assert.Equal(ReleaseMask.None, Key(VkMenu, up: false, scan: 0x38));
        Assert.Equal(ReleaseMask.None, Key(VkShift, up: false));
        Assert.Equal(ReleaseMask.None, Key(VkF11, up: false));
        Assert.Equal(ReleaseMask.None, Key(VkF11, up: true));
        Assert.Equal(ReleaseMask.None, Key(VkShift, up: true));

        // Alt goes up while Ctrl is still held: swallowed now, sent right after Ctrl's release.
        Assert.Equal(new ReleaseMask(Swallow: true, Release: null), Key(VkMenu, up: true, scan: 0x38));
        Assert.Equal(new ReleaseMask(Swallow: false, Release: new MaskedKey(VkMenu, 0x38, false)), Key(VkControl, up: true));

        // The next Alt tap is the user's own and is left alone.
        Key(VkMenu, up: false);
        Assert.Equal(ReleaseMask.None, Key(VkMenu, up: true));
    }

    [Fact]
    public void Alt_released_last_is_sent_at_once()
    {
        Key(0xA2, up: false);
        Key(0xA4, up: false);
        Key(VkF11 - 1, up: false);
        Key(VkF11 - 1, up: true);
        Key(0xA2, up: true);

        // Ctrl+Alt+F10 is not registered, so nothing is masked.
        Assert.Equal(ReleaseMask.None, Key(0xA4, up: true));

        Key(0xA2, up: false);
        Key(0xA4, up: false);
        Key(0xA0, up: false);
        Key(VkF11, up: false);
        Key(0xA0, up: true);
        Key(0xA2, up: true);
        Assert.Equal(new ReleaseMask(Swallow: true, Release: new MaskedKey(0xA4, 0x38, false)), Key(0xA4, up: true, scan: 0x38));
    }

    [Fact]
    public void Win_release_after_a_win_hotkey_is_masked()
    {
        Key(0x5B, up: false);
        Assert.True(_state.WinDown);
        Key(0x4E, up: false);
        Key(0x4E, up: true);

        Assert.Equal(new ReleaseMask(Swallow: true, Release: new MaskedKey(0x5B, 0, false)), Key(0x5B, up: true));
        Assert.False(_state.WinDown);
    }

    [Theory]
    [InlineData(0x51)]
    [InlineData(VkF11)]
    public void Hotkeys_without_alt_or_win_and_unregistered_keys_are_not_masked(int key)
    {
        // Ctrl+Shift+Q is registered but has no Alt or Win; Ctrl+Shift+F11 is not registered.
        Key(0xA2, up: false);
        Key(0xA0, up: false);
        Key(key, up: false);
        Key(key, up: true);

        Assert.Equal(ReleaseMask.None, Key(0xA0, up: true));
        Assert.Equal(ReleaseMask.None, Key(0xA2, up: true));
    }

    [Fact]
    public void Another_key_after_the_hotkey_cancels_the_mask()
    {
        Key(0xA4, up: false);
        Key(0xA2, up: false);
        Key(0xA0, up: false);
        Key(VkF11, up: false);
        Key(0x41, up: false);
        Key(0xA2, up: true);
        Key(0xA0, up: true);

        Assert.Equal(ReleaseMask.None, Key(0xA4, up: true));
    }

    [Fact]
    public void Resync_replaces_the_tracked_state()
    {
        Key(VkControl, up: false);

        _state.Resync(ModifierSides.RightAlt, win: true);

        Assert.Equal(ModifierSides.RightAlt, _state.Sides);
        Assert.True(_state.WinDown);
        Assert.Equal(KeyStrokeAttributes.Alt, _state.Flags);
        Assert.Throws<ArgumentNullException>(() => _state.Decide(0x41, 0, extended: false, up: false, null!));
    }
}
