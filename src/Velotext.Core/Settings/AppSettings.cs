// Velotext: user preferences stored in settings.json.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Model;

namespace Velotext.Core.Settings;

/// <summary>Colour theme of the Velotext windows.</summary>
public enum AppTheme
{
    /// <summary>Dark theme (default).</summary>
    Dark = 0,

    /// <summary>Light theme.</summary>
    Light = 1,

    /// <summary>Follow the Windows app theme.</summary>
    System = 2,
}

/// <summary>Options that new autotext triggers start with. All on by default.</summary>
public sealed record TriggerDefaults
{
    /// <summary>New triggers match case exactly.</summary>
    public bool CaseSensitive { get; init; } = true;

    /// <summary>New triggers expand as soon as the last character is typed.</summary>
    public bool Immediate { get; init; } = true;

    /// <summary>New triggers fire only at the start of a word.</summary>
    public bool WordStart { get; init; } = true;
}

/// <summary>User preferences. Every value has a safe default so a missing or partial file still works.</summary>
public sealed record AppSettings
{
    /// <summary>Schema version written by this build.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Smallest allowed paste timeout, in milliseconds.</summary>
    public const int MinPasteTimeoutMilliseconds = 300;

    /// <summary>Largest allowed paste timeout, in milliseconds.</summary>
    public const int MaxPasteTimeoutMilliseconds = 10000;

    /// <summary>Schema version of the stored file.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Start Velotext when the user signs in to Windows.</summary>
    public bool StartWithWindows { get; init; } = true;

    /// <summary>Backspace right after an expansion undoes it.</summary>
    public bool UndoWithBackspace { get; init; } = true;

    /// <summary>Options for new autotext triggers.</summary>
    public TriggerDefaults NewTriggerDefaults { get; init; } = new();

    /// <summary>Optional global hotkey that pauses and resumes expansion.</summary>
    public HotkeyGesture? PauseHotkey { get; init; }

    /// <summary>Font family used for formatted text in applications that need one (Word, classic Outlook).</summary>
    public string RichTextFontName { get; init; } = "Aptos";

    /// <summary>Font size in points used for formatted text in applications that need one.</summary>
    public double RichTextFontSize { get; init; } = 11;

    /// <summary>
    /// How long to wait for the target application to read a pasted phrase before the user's clipboard
    /// is restored anyway. Normally the restore happens as soon as the target has read it.
    /// </summary>
    public int PasteTimeoutMilliseconds { get; init; } = 1500;

    /// <summary>Colour theme.</summary>
    public AppTheme Theme { get; init; } = AppTheme.Dark;

    /// <summary>True once the user has seen the "still running in the tray" hint.</summary>
    public bool TrayHintShown { get; init; }

    /// <summary>Returns a copy with out-of-range values replaced by safe ones.</summary>
    public AppSettings Normalize() => this with
    {
        NewTriggerDefaults = NewTriggerDefaults ?? new TriggerDefaults(),
        RichTextFontName = string.IsNullOrWhiteSpace(RichTextFontName) ? "Aptos" : RichTextFontName.Trim(),
        RichTextFontSize = double.IsFinite(RichTextFontSize) ? Math.Clamp(RichTextFontSize, 6, 72) : 11,
        PasteTimeoutMilliseconds = Math.Clamp(PasteTimeoutMilliseconds, MinPasteTimeoutMilliseconds, MaxPasteTimeoutMilliseconds),
        Theme = Enum.IsDefined(Theme) ? Theme : AppTheme.Dark,
        PauseHotkey = PauseHotkey is { } gesture && gesture.ValidationError is null ? gesture : null,
    };
}
