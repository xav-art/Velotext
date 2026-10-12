// Velotext: starts and wires the expansion machinery (keyboard monitor, engine, clipboard, hotkeys) to the app.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Velotext.Core.Input;
using Velotext.Core.Matching;
using Velotext.Core.Model;
using Velotext.Core.RichText;
using Velotext.Core.Settings;
using Velotext.Core.Usage;
using Velotext.Platform.Windows.Clipboard;
using Velotext.Platform.Windows.Expansion;
using Velotext.Platform.Windows.Hotkeys;
using Velotext.Platform.Windows.Input;

namespace Velotext.App.Services;

/// <summary>How serious a notification is.</summary>
public enum NotificationKind
{
    /// <summary>Information, for example "copied to the clipboard".</summary>
    Info = 0,

    /// <summary>Something did not work as expected.</summary>
    Warning = 1,
}

/// <summary>Shows short notifications to the user (tray balloons).</summary>
public interface INotificationService
{
    /// <summary>Shows a notification. Safe to call from any thread.</summary>
    /// <param name="title">Short title.</param>
    /// <param name="message">One or two sentences.</param>
    /// <param name="kind">Severity.</param>
    void Show(string title, string message, NotificationKind kind);
}

/// <summary>
/// Runs the expansion machinery: it feeds triggers and hotkeys from the library to the keyboard monitor
/// and hotkey service, applies settings, tracks the paused state and re-installs the keyboard hook after
/// sleep or unlock (Windows can silently drop low-level hooks).
/// </summary>
public sealed partial class ExpansionRuntime : IExpansionHost, ViewModels.IPauseControl, IDisposable
{
    private static readonly Guid s_pauseHotkeyKey = new("5f1c2b7e-4b8e-4a77-9a3c-0d6f1f2b9e11");

    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly UsageService _usage;
    private readonly INotificationService _notifications;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<ExpansionRuntime> _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private ClipboardService? _clipboard;
    private ExpansionEngine? _engine;
    private KeyboardMonitor? _monitor;
    private GlobalHotkeyService? _hotkeys;
    private IReadOnlyList<HotkeyRegistration> _registered = [];
    private ExpansionSettings _expansionSettings;
    private bool _paused;

    /// <summary>Creates the runtime; call <see cref="Start"/> to begin watching the keyboard.</summary>
    /// <param name="library">The library service.</param>
    /// <param name="settings">The settings service.</param>
    /// <param name="usage">The usage service.</param>
    /// <param name="notifications">User notifications.</param>
    /// <param name="loggers">Logger factory.</param>
    /// <param name="time">Clock.</param>
    public ExpansionRuntime(LibraryService library, SettingsService settings, UsageService usage, INotificationService notifications, ILoggerFactory loggers, TimeProvider time)
    {
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _usage = usage ?? throw new ArgumentNullException(nameof(usage));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _loggers = loggers ?? throw new ArgumentNullException(nameof(loggers));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = loggers.CreateLogger<ExpansionRuntime>();
        _expansionSettings = ToExpansionSettings(settings.Current);
    }

    /// <summary>Raised when expansion is paused or resumed, on any thread.</summary>
    public event EventHandler<bool>? PausedChanged;

    /// <summary>Raised when the pause hotkey could or could not be registered, on a background thread.</summary>
    public event EventHandler<bool>? PauseHotkeyConflictChanged;

    /// <summary>True while expansion is paused.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_gate)
            {
                return _paused;
            }
        }
    }

    /// <summary>True when another app already owns the pause hotkey.</summary>
    public bool PauseHotkeyConflict { get; private set; }

    /// <inheritdoc />
    public ExpansionSettings Settings
    {
        get
        {
            lock (_gate)
            {
                return _expansionSettings;
            }
        }
    }

    /// <summary>Starts the keyboard monitor, engine and hotkeys.</summary>
    public void Start()
    {
        _clipboard = new ClipboardService(_loggers.CreateLogger<ClipboardService>());
        _engine = new ExpansionEngine(this, _clipboard, new InputInjector(), new WindowsSystemState(), _time, _loggers.CreateLogger<ExpansionEngine>());

        var options = new InputSequencerOptions { OperationTimeout = OperationBudget(_settings.Current) };
        _monitor = new KeyboardMonitor(_engine, _time, options, _loggers.CreateLogger<KeyboardMonitor>());
        _engine.Attach(_monitor);
        _hotkeys = new GlobalHotkeyService(_loggers.CreateLogger<GlobalHotkeyService>());
        _hotkeys.Pressed += OnHotkeyPressed;

        ApplySettings(_settings.Current);
        _settings.Changed += OnSettingsChanged;
        _library.Changed += OnLibraryChanged;
        OnLibraryChanged(this, _library.Snapshot);

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        LogStarted(_logger);
    }

    /// <summary>Pauses or resumes expansion.</summary>
    /// <param name="paused">True to pause.</param>
    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused)
            {
                return;
            }

            _paused = paused;
        }

        // Two quick toggles from different threads can arrive in any order, so every step reads the
        // current state instead of the value it was called with.
        _monitor?.Post(sequencer => sequencer.Enabled = !IsPaused);
        LogPaused(_logger, paused);
        PausedChanged?.Invoke(this, IsPaused);
    }

    /// <summary>Explains why a hotkey cannot be used because it types a character on this keyboard, or returns <c>null</c>.</summary>
    /// <param name="gesture">The hotkey.</param>
    public static string? DescribeTypingConflict(HotkeyGesture gesture) =>
        KeyboardLayoutProbe.TypedCharacter(gesture) is { } typed
            ? $"On your keyboard, {gesture} types {typed}. Choose a different hotkey."
            : null;

    /// <summary>Explains why the pause hotkey cannot be used, or returns <c>null</c>.</summary>
    /// <param name="gesture">The pause hotkey.</param>
    public string? DescribePauseHotkeyProblem(HotkeyGesture gesture) =>
        gesture.ValidationError
        ?? DescribeTypingConflict(gesture)
        ?? (PauseHotkeyConflict && _settings.Current.PauseHotkey == gesture ? $"Another app or Windows already uses {gesture}. Choose a different hotkey." : null);

    /// <inheritdoc />
    public Phrase? FindPhrase(Guid id) => _library.Snapshot.Phrases.GetValueOrDefault(id);

    /// <inheritdoc />
    public void PhraseInserted(Guid id, int typedCharacters) =>
        _usage.RecordUse(id, FindPhrase(id) is { } phrase ? TypingSavings.CharactersSaved(phrase.Content, typedCharacters) : 0);

    /// <inheritdoc />
    public void Notify(ExpansionNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var phrase = notice.PhraseDescription;
        switch (notice.Kind)
        {
            case ExpansionNoticeKind.CopiedToClipboard:
                _notifications.Show("Copied to the clipboard", $"\"{phrase}\" is on the clipboard. Press Ctrl+V to paste it.", NotificationKind.Info);
                break;
            case ExpansionNoticeKind.PasteNotConfirmed:
                _notifications.Show("Phrase may be missing", $"\"{phrase}\" may not be in the app. If the text is missing, set Insert into to Clipboard only for this phrase.", NotificationKind.Warning);
                break;
            case ExpansionNoticeKind.ClipboardBusy when notice.TriggerRestored:
                _notifications.Show("Clipboard in use", "Another app is using the clipboard. Velotext typed your trigger back. Try again.", NotificationKind.Warning);
                break;
            case ExpansionNoticeKind.ClipboardBusy:
                _notifications.Show("Clipboard in use", $"Another app is using the clipboard. Velotext did not insert \"{phrase}\". Try again.", NotificationKind.Warning);
                break;
            case ExpansionNoticeKind.WindowChanged:
                _notifications.Show("Phrase not inserted", $"You clicked or switched windows while Velotext inserted \"{phrase}\", so it stopped. Check the text, then type the trigger again.", NotificationKind.Warning);
                break;
            case ExpansionNoticeKind.Expired:
                _notifications.Show("Phrase not inserted", $"The clipboard did not respond in time, so Velotext stopped inserting \"{phrase}\". Check the text, then type the trigger again.", NotificationKind.Warning);
                break;
            case ExpansionNoticeKind.DelimiterSkipped:
                _notifications.Show("Phrase inserted, last key skipped", $"Velotext inserted \"{phrase}\", but you switched windows before it could type the key that followed the trigger. Check the text.", NotificationKind.Warning);
                break;
            case ExpansionNoticeKind.TargetBusy when notice.TriggerRestored:
                _notifications.Show("App still busy", $"The app has not taken the previous phrase yet, so Velotext did not insert \"{phrase}\" and typed your trigger back. Try again in a moment.", NotificationKind.Warning);
                break;
            case ExpansionNoticeKind.TargetBusy:
                _notifications.Show("App still busy", $"The app has not taken the previous phrase yet, so Velotext did not insert \"{phrase}\". Try again in a moment.", NotificationKind.Warning);
                break;
            case ExpansionNoticeKind.KeysDiscarded:
                _notifications.Show(
                    "Some keys not typed",
                    notice.KeyCount == 1
                        ? "You clicked or switched windows while Velotext inserted a phrase, so it did not type the 1 key that you pressed meanwhile. Type it again."
                        : $"You clicked or switched windows while Velotext inserted a phrase, so it did not type the {notice.KeyCount} keys that you pressed meanwhile. Type them again.",
                    NotificationKind.Warning);
                break;
        }
    }

    /// <summary>Stops hooks, hotkeys and the engine.</summary>
    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _library.Changed -= OnLibraryChanged;
        _settings.Changed -= OnSettingsChanged;
        // Order matters. The engine stops first: cancelled operations give the clipboard back and release
        // the keys typed meanwhile. The monitor then delivers any keys still held before it removes the
        // hook, and the clipboard service restores a paste that still waits for a late read.
        _hotkeys?.Dispose();
        _engine?.Dispose();
        _monitor?.Dispose();
        _clipboard?.Dispose();
    }

    /// <summary>
    /// Longest an operation may hold the user's typing: the paste wait from settings plus the fixed steps
    /// (modifier wait 1 s, up to 3 clipboard opens of about 0.4 s, reader wait and grace about 1.1 s),
    /// with a margin, so the engine always finishes before the hold is released.
    /// </summary>
    private static TimeSpan OperationBudget(AppSettings settings) =>
        TimeSpan.FromMilliseconds(settings.PasteTimeoutMilliseconds) + TimeSpan.FromSeconds(5);

    private static ExpansionSettings ToExpansionSettings(AppSettings settings) => new(
        settings.UndoWithBackspace,
        TimeSpan.FromMilliseconds(settings.PasteTimeoutMilliseconds),
        new RtfOptions(settings.RichTextFontName, settings.RichTextFontSize));

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        ApplySettings(settings);
        _ = RegisterHotkeysAsync(_library.Snapshot.Library);
    }

    private void ApplySettings(AppSettings settings)
    {
        lock (_gate)
        {
            _expansionSettings = ToExpansionSettings(settings);
        }

        var budget = OperationBudget(settings);
        _monitor?.Post(sequencer =>
        {
            sequencer.UndoEnabled = settings.UndoWithBackspace;
            sequencer.OperationTimeout = budget;
        });
    }

    private void OnLibraryChanged(object? sender, LibrarySnapshot snapshot)
    {
        _monitor?.UpdateTriggers(TriggerIndex.FromLibrary(snapshot.Library));
        _ = RegisterHotkeysAsync(snapshot.Library);
    }

    private async Task RegisterHotkeysAsync(PhraseLibrary library)
    {
        if (_hotkeys is null)
        {
            return;
        }

        // The first phrase with a gesture owns it, matching what validation reports for duplicates.
        // Combinations that type a character on this keyboard (AltGr) are never registered.
        var registrations = new List<HotkeyRegistration>();
        var typing = new List<HotkeyConflict>();
        var seen = new HashSet<HotkeyGesture>();
        foreach (var phrase in library.EnumeratePhrases())
        {
            foreach (var hotkey in phrase.Triggers.OfType<HotkeyTrigger>())
            {
                if (hotkey.Gesture.ValidationError is not null || !seen.Add(hotkey.Gesture))
                {
                    continue;
                }

                if (DescribeTypingConflict(hotkey.Gesture) is { } message)
                {
                    typing.Add(new HotkeyConflict(hotkey.Gesture, message));
                }
                else
                {
                    registrations.Add(new HotkeyRegistration(phrase.Id, hotkey.Gesture));
                }
            }
        }

        if (_settings.Current.PauseHotkey is { } pause && DescribeTypingConflict(pause) is null && seen.Add(pause))
        {
            registrations.Add(new HotkeyRegistration(s_pauseHotkeyKey, pause));
        }

        lock (_gate)
        {
            if (registrations.SequenceEqual(_registered))
            {
                return;
            }

            _registered = registrations;
        }

        _monitor?.UpdateHotkeys(registrations.Select(r => r.Gesture));
        try
        {
            var failed = await _hotkeys.ReplaceAllAsync(registrations).ConfigureAwait(false);
            var pauseConflict = _settings.Current.PauseHotkey is { } p && failed.Contains(p);
            if (pauseConflict != PauseHotkeyConflict)
            {
                PauseHotkeyConflict = pauseConflict;
                PauseHotkeyConflictChanged?.Invoke(this, pauseConflict);
            }

            _library.ReportHotkeyConflicts([
                .. typing,
                .. failed.Where(g => g != _settings.Current.PauseHotkey)
                    .Select(g => new HotkeyConflict(g, $"Another app or Windows already uses {g}. Choose a different hotkey.")),
            ]);
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    private void OnHotkeyPressed(object? sender, Guid key)
    {
        if (key == s_pauseHotkeyKey)
        {
            var paused = !IsPaused;
            SetPaused(paused);

            // The hotkey works with the window hidden, so confirm the new state where the user can see it.
            _notifications.Show(
                paused ? "Expansion paused" : "Expansion resumed",
                paused ? $"Press {_settings.Current.PauseHotkey} to resume." : "Velotext expands your triggers again.",
                NotificationKind.Info);
            return;
        }

        if (!IsPaused && _engine is not null)
        {
            _ = _engine.InsertFromHotkeyAsync(key);
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
        {
            _ = ReinstallAsync();
        }
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            _ = ReinstallAsync();
        }
    }

    private async Task ReinstallAsync()
    {
        if (_monitor is null)
        {
            return;
        }

        try
        {
            await _monitor.ReinstallAsync().ConfigureAwait(false);
            LogReinstalled(_logger);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or System.ComponentModel.Win32Exception)
        {
            LogReinstallFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Expansion runtime started.")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Expansion paused: {Paused}.")]
    private static partial void LogPaused(ILogger logger, bool paused);

    [LoggerMessage(Level = LogLevel.Information, Message = "Keyboard hook re-installed after unlock or resume.")]
    private static partial void LogReinstalled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Keyboard hook could not be re-installed.")]
    private static partial void LogReinstallFailed(ILogger logger, Exception exception);
}
