// Velotext: the modifier keys the user physically holds, and the rule that masks Alt or Win after a hotkey.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Input;
using Velotext.Core.Model;

namespace Velotext.Platform.Windows.Input;

/// <summary>What the hook does with an event after a hotkey with Alt or Win.</summary>
/// <param name="Swallow">True when the event must not reach the app (an Alt or Win release that is sent later).</param>
/// <param name="Release">The Alt or Win key whose release must be sent now, behind a masking key, or <c>null</c>.</param>
internal readonly record struct ReleaseMask(bool Swallow, MaskedKey? Release)
{
    /// <summary>Nothing to do: the event passes.</summary>
    public static ReleaseMask None { get; } = new(false, null);
}

/// <summary>An Alt or Win key whose release Velotext sends itself.</summary>
/// <param name="VirtualKey">The virtual key.</param>
/// <param name="ScanCode">The scan code.</param>
/// <param name="Extended">True for extended keys.</param>
internal readonly record struct MaskedKey(int VirtualKey, int ScanCode, bool Extended);

/// <summary>
/// Tracks Shift, Ctrl, Alt (per side) and Win from the keyboard events the hook sees. Keyboards report the
/// side-specific keys (left Ctrl, right Alt); software often sends the generic ones (Ctrl, Alt), so both
/// count. It also decides how the release of Alt or Win is sent after a registered hotkey with Alt or Win:
/// the hotkey's own key never reaches the app, so the app would see only modifiers, take the Alt or Win
/// release as a tap, and open its menu bar or the Start menu. The release is sent behind a masking key, and
/// only after the other modifiers are released: apps with modern menus also treat Alt released while Ctrl
/// is still held as a tap. Used on the keyboard hook thread only.
/// </summary>
internal sealed class PhysicalKeyboardState
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLeftShift = 0xA0;
    private const int VkLeftWin = 0x5B;
    private const int VkRightWin = 0x5C;
    private const int RightShiftScanCode = 0x36;

    private int _sides;
    private bool _win;
    private bool _maskNextAltOrWinRelease;
    private MaskedKey? _deferredRelease;

    /// <summary>Shift, Ctrl and Alt held now, per side. Safe to read from any thread.</summary>
    public ModifierSides Sides => (ModifierSides)Volatile.Read(ref _sides);

    /// <summary>True while a Win key is held.</summary>
    public bool WinDown => _win;

    /// <summary>The held modifiers as key stroke attributes (either side counts).</summary>
    public KeyStrokeAttributes Flags
    {
        get
        {
            var held = Sides;
            return ((held & (ModifierSides.LeftShift | ModifierSides.RightShift)) != 0 ? KeyStrokeAttributes.Shift : KeyStrokeAttributes.None)
                | ((held & (ModifierSides.LeftControl | ModifierSides.RightControl)) != 0 ? KeyStrokeAttributes.Control : KeyStrokeAttributes.None)
                | ((held & (ModifierSides.LeftAlt | ModifierSides.RightAlt)) != 0 ? KeyStrokeAttributes.Alt : KeyStrokeAttributes.None);
        }
    }

    /// <summary>Replaces the tracked state with Windows' own key state, which is exact while nothing is held back.</summary>
    /// <param name="sides">Shift, Ctrl and Alt per side, as Windows reports them.</param>
    /// <param name="win">True when Windows reports a Win key down.</param>
    public void Resync(ModifierSides sides, bool win)
    {
        Volatile.Write(ref _sides, (int)sides);
        _win = win;
    }

    /// <summary>Updates the state with one physical key event.</summary>
    /// <param name="virtualKey">The virtual key.</param>
    /// <param name="scanCode">The scan code (it tells right Shift from left Shift for the generic key).</param>
    /// <param name="extended">True for extended keys (right Ctrl, right Alt).</param>
    /// <param name="up">True for a release.</param>
    public void Apply(int virtualKey, int scanCode, bool extended, bool up)
    {
        var side = SideOf(virtualKey, scanCode, extended);
        if (side != ModifierSides.None)
        {
            Volatile.Write(ref _sides, up ? _sides & ~(int)side : _sides | (int)side);
        }
        else if (virtualKey is VkLeftWin or VkRightWin)
        {
            _win = !up;
        }
    }

    /// <summary>
    /// Decides, after <see cref="Apply"/>, what happens to this event. A key press that matches a registered
    /// hotkey with Alt or Win arms the mask for the next Alt or Win release. That release is swallowed; it is
    /// sent at once when no other modifier is held, or else right after the last other modifier is released.
    /// </summary>
    /// <param name="virtualKey">The virtual key.</param>
    /// <param name="scanCode">The scan code.</param>
    /// <param name="extended">True for extended keys.</param>
    /// <param name="up">True for a release.</param>
    /// <param name="hotkeys">The registered hotkeys.</param>
    public ReleaseMask Decide(int virtualKey, int scanCode, bool extended, bool up, IReadOnlySet<HotkeyGesture> hotkeys)
    {
        ArgumentNullException.ThrowIfNull(hotkeys);
        var isAltOrWin = virtualKey is VkMenu or 0xA4 or 0xA5 or VkLeftWin or VkRightWin;
        var isModifier = isAltOrWin || virtualKey is VkShift or VkControl or (>= VkLeftShift and <= VkLeftShift + 3);
        var othersHeld = Sides != ModifierSides.None || _win;
        if (!up && !isModifier)
        {
            var flags = Flags;
            var modifiers = ((flags & KeyStrokeAttributes.Control) != 0 ? HotkeyModifiers.Control : HotkeyModifiers.None)
                | ((flags & KeyStrokeAttributes.Alt) != 0 ? HotkeyModifiers.Alt : HotkeyModifiers.None)
                | ((flags & KeyStrokeAttributes.Shift) != 0 ? HotkeyModifiers.Shift : HotkeyModifiers.None)
                | (_win ? HotkeyModifiers.Windows : HotkeyModifiers.None);
            _maskNextAltOrWinRelease = (modifiers & (HotkeyModifiers.Alt | HotkeyModifiers.Windows)) != 0
                && hotkeys.Contains(new HotkeyGesture(modifiers, virtualKey));
            return ReleaseMask.None;
        }

        if (up && isAltOrWin && _maskNextAltOrWinRelease)
        {
            _maskNextAltOrWinRelease = false;
            var key = new MaskedKey(virtualKey, scanCode, extended);
            if (othersHeld)
            {
                _deferredRelease = key;
                return new ReleaseMask(Swallow: true, Release: null);
            }

            return new ReleaseMask(Swallow: true, Release: key);
        }

        if (up && isModifier && !othersHeld && _deferredRelease is { } deferred)
        {
            // The last other modifier is up: it passes, and the held-back release follows it.
            _deferredRelease = null;
            return new ReleaseMask(Swallow: false, Release: deferred);
        }

        return ReleaseMask.None;
    }

    /// <summary>The side of a Shift, Ctrl or Alt key, for specific and generic virtual keys; none for other keys.</summary>
    /// <param name="virtualKey">The virtual key.</param>
    /// <param name="scanCode">The scan code.</param>
    /// <param name="extended">True for extended keys.</param>
    internal static ModifierSides SideOf(int virtualKey, int scanCode, bool extended) => virtualKey switch
    {
        >= VkLeftShift and <= VkLeftShift + 5 => (ModifierSides)(1 << (virtualKey - VkLeftShift)),
        VkShift => scanCode == RightShiftScanCode ? ModifierSides.RightShift : ModifierSides.LeftShift,
        VkControl => extended ? ModifierSides.RightControl : ModifierSides.LeftControl,
        VkMenu => extended ? ModifierSides.RightAlt : ModifierSides.LeftAlt,
        _ => ModifierSides.None,
    };
}
