// Velotext: the settings window's view model; every change is applied and saved at once.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Velotext.App.Services;
using Velotext.Core.Model;
using Velotext.Core.Settings;
using Velotext.Platform.Windows.Lifecycle;

namespace Velotext.App.ViewModels;

/// <summary>Application actions the settings window needs.</summary>
/// <param name="ExecutablePath">Path of the running Velotext executable (for autostart).</param>
/// <param name="DataDirectory">The data folder.</param>
/// <param name="LogDirectory">The log folder.</param>
/// <param name="OpenFolder">Opens a folder in File Explorer.</param>
/// <param name="ApplyTheme">Applies a theme.</param>
/// <param name="ReplaceLibrary">Replaces the library (restore from backup).</param>
/// <param name="DescribePauseHotkeyProblem">Explains why a pause hotkey cannot be used, or returns <c>null</c>.</param>
public sealed record SettingsActions(
    string ExecutablePath,
    string DataDirectory,
    string LogDirectory,
    Action<string> OpenFolder,
    Action<AppTheme> ApplyTheme,
    Action<PhraseLibrary> ReplaceLibrary,
    Func<HotkeyGesture, string?> DescribePauseHotkeyProblem);

/// <summary>State of the settings window. Each property change is saved immediately.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly AutostartService _autostart;
    private readonly LibraryService _library;
    private readonly IDialogService _dialogs;
    private readonly SettingsActions _actions;
    private readonly bool _initialized;

    /// <summary>Start Velotext when the user signs in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutostartNote))]
    private bool _startWithWindows;

    /// <summary>Theme: 0 dark, 1 light, 2 follow Windows.</summary>
    [ObservableProperty]
    private int _themeIndex;

    /// <summary>Backspace right after an expansion undoes it.</summary>
    [ObservableProperty]
    private bool _undoWithBackspace;

    /// <summary>New typed-text triggers match case.</summary>
    [ObservableProperty]
    private bool _defaultCaseSensitive;

    /// <summary>New typed-text triggers expand immediately.</summary>
    [ObservableProperty]
    private bool _defaultImmediate;

    /// <summary>New typed-text triggers fire only at the start of a word.</summary>
    [ObservableProperty]
    private bool _defaultWordStart;

    /// <summary>Hotkey that pauses and resumes expansion.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PauseHotkeyProblem))]
    private HotkeyGesture? _pauseHotkey;

    /// <summary>Font for formatted phrases in Word and Outlook.</summary>
    [ObservableProperty]
    private string _fontName;

    /// <summary>Font size in points.</summary>
    [ObservableProperty]
    private double _fontSize;

    /// <summary>Longest wait for an app to read a pasted phrase, in seconds.</summary>
    [ObservableProperty]
    private double _pasteTimeoutSeconds;

    /// <summary>Creates the view model from the current settings.</summary>
    /// <param name="settings">Settings service.</param>
    /// <param name="autostart">Autostart service.</param>
    /// <param name="library">Library service (backups).</param>
    /// <param name="dialogs">Dialogs.</param>
    /// <param name="actions">Application actions.</param>
    public SettingsViewModel(SettingsService settings, AutostartService autostart, LibraryService library, IDialogService dialogs, SettingsActions actions)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _autostart = autostart ?? throw new ArgumentNullException(nameof(autostart));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));

        var current = settings.Current;
        _startWithWindows = autostart.GetState(actions.ExecutablePath) == AutostartState.On;
        _themeIndex = (int)current.Theme;
        _undoWithBackspace = current.UndoWithBackspace;
        _defaultCaseSensitive = current.NewTriggerDefaults.CaseSensitive;
        _defaultImmediate = current.NewTriggerDefaults.Immediate;
        _defaultWordStart = current.NewTriggerDefaults.WordStart;
        _pauseHotkey = current.PauseHotkey;
        _fontName = current.RichTextFontName;
        _fontSize = current.RichTextFontSize;
        _pasteTimeoutSeconds = current.PasteTimeoutMilliseconds / 1000.0;
        _initialized = true;
    }

    /// <summary>Explains when Task Manager blocks autostart, or <c>null</c>.</summary>
    public string? AutostartNote => !StartWithWindows && _autostart.GetState(_actions.ExecutablePath) == AutostartState.DisabledInTaskManager
        ? "Startup is turned off in Task Manager. Turn this on to start Velotext with Windows again."
        : null;

    /// <summary>A problem with the pause hotkey, or <c>null</c>.</summary>
    public string? PauseHotkeyProblem => PauseHotkey is { } gesture ? _actions.DescribePauseHotkeyProblem(gesture) : null;

    /// <summary>The data folder, shown so users and IT can find it.</summary>
    public string DataDirectory => _actions.DataDirectory;

    /// <summary>The Velotext version.</summary>
    public static string Version => string.Create(CultureInfo.InvariantCulture, $"Version {typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3)}");

    /// <summary>Installed font families for the font picker.</summary>
    public IReadOnlyList<string> FontNames { get; } = [.. System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Order(StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Re-reads the pause hotkey problem (after hotkey registration finished).</summary>
    public void RefreshPauseHotkeyProblem() => OnPropertyChanged(nameof(PauseHotkeyProblem));

    /// <summary>Opens the data folder.</summary>
    [RelayCommand]
    private void OpenDataFolder() => _actions.OpenFolder(_actions.DataDirectory);

    /// <summary>Opens the log folder.</summary>
    [RelayCommand]
    private void OpenLogFolder() => _actions.OpenFolder(_actions.LogDirectory);

    /// <summary>Replaces the library with a backup the user picks.</summary>
    [RelayCommand]
    private void RestoreBackup()
    {
        var path = _dialogs.PickFileToOpen("Velotext backups (*.json)|*.json", _library.BackupDirectory);
        if (path is null)
        {
            return;
        }

        var backup = _library.ReadBackup(path);
        if (backup is null)
        {
            _dialogs.ShowError("The backup was not restored", "Velotext cannot read this file. Choose another backup.");
            return;
        }

        var count = backup.EnumeratePhrases().Count();
        var holds = count == 1 ? "1 phrase" : string.Create(CultureInfo.InvariantCulture, $"{count} phrases");
        if (_dialogs.Confirm(
            "Replace your phrases with this backup?",
            $"The backup holds {holds}. Velotext replaces all your current phrases. It saves them in a new backup first.",
            "Replace phrases",
            destructive: true))
        {
            _actions.ReplaceLibrary(backup);
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            _autostart.Set(_actions.ExecutablePath, value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _dialogs.ShowError("Startup setting not changed", "Windows did not allow the change. Your organization may manage startup apps. Ask your IT contact.");
        }

        Save(s => s with { StartWithWindows = value });
    }

    partial void OnThemeIndexChanged(int value)
    {
        var theme = Enum.IsDefined((AppTheme)value) ? (AppTheme)value : AppTheme.Dark;
        Save(s => s with { Theme = theme });
        if (_initialized)
        {
            _actions.ApplyTheme(theme);
        }
    }

    partial void OnUndoWithBackspaceChanged(bool value) => Save(s => s with { UndoWithBackspace = value });

    partial void OnDefaultCaseSensitiveChanged(bool value) => Save(s => s with { NewTriggerDefaults = s.NewTriggerDefaults with { CaseSensitive = value } });

    partial void OnDefaultImmediateChanged(bool value) => Save(s => s with { NewTriggerDefaults = s.NewTriggerDefaults with { Immediate = value } });

    partial void OnDefaultWordStartChanged(bool value) => Save(s => s with { NewTriggerDefaults = s.NewTriggerDefaults with { WordStart = value } });

    partial void OnPauseHotkeyChanged(HotkeyGesture? value)
    {
        if (value is null || value.Value.ValidationError is null)
        {
            Save(s => s with { PauseHotkey = value });
        }
    }

    partial void OnFontNameChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Save(s => s with { RichTextFontName = value.Trim() });
        }
    }

    partial void OnFontSizeChanged(double value) => Save(s => s with { RichTextFontSize = value });

    partial void OnPasteTimeoutSecondsChanged(double value) =>
        Save(s => s with { PasteTimeoutMilliseconds = (int)Math.Round(value * 1000) });

    private void Save(Func<AppSettings, AppSettings> change)
    {
        if (_initialized)
        {
            _settings.Update(change);
        }
    }
}
