// Velotext: a text box that records a key combination when the user presses it.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Velotext.Core.Model;

namespace Velotext.App.Editing;

/// <summary>
/// Records a hotkey: focus the box and press the combination. Backspace or Delete clears it; Tab and
/// Shift+Tab still move focus so keyboard users are never trapped.
/// </summary>
public sealed class HotkeyBox : TextBox
{
    /// <summary>The recorded combination, or <c>null</c>.</summary>
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture),
        typeof(HotkeyGesture?),
        typeof(HotkeyBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((HotkeyBox)d).UpdateText()));

    /// <summary>Creates the box.</summary>
    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        Cursor = Cursors.Arrow;
        UpdateText();
    }

    /// <summary>The recorded combination, or <c>null</c>.</summary>
    public HotkeyGesture? Gesture
    {
        get => (HotkeyGesture?)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    /// <summary>Maps WPF modifier state to hotkey modifiers.</summary>
    /// <param name="modifiers">WPF modifiers.</param>
    /// <param name="windowsKeyDown">True when a Windows key is held.</param>
    internal static HotkeyModifiers ToHotkeyModifiers(ModifierKeys modifiers, bool windowsKeyDown)
    {
        var result = HotkeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result |= HotkeyModifiers.Control;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            result |= HotkeyModifiers.Alt;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            result |= HotkeyModifiers.Shift;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows) || windowsKeyDown)
        {
            result |= HotkeyModifiers.Windows;
        }

        return result;
    }

    /// <inheritdoc />
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            _ => e.Key,
        };

        var modifiers = Keyboard.Modifiers;
        if (key == Key.Tab && (modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            return;
        }

        e.Handled = true;
        if ((key is Key.Back or Key.Delete or Key.Escape) && modifiers == ModifierKeys.None)
        {
            if (key != Key.Escape)
            {
                Gesture = null;
            }

            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
        {
            return;
        }

        var windows = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
        Gesture = new HotkeyGesture(ToHotkeyModifiers(modifiers, windows), KeyInterop.VirtualKeyFromKey(key));
    }

    /// <inheritdoc />
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        UpdateText();
    }

    /// <inheritdoc />
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        UpdateText();
    }

    private void UpdateText() =>
        Text = Gesture?.ToString() ?? (IsKeyboardFocused ? "Press the keys" : "Not set");
}
