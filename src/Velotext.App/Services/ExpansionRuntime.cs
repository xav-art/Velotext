// Velotext: starts and wires the expansion machinery (keyboard monitor, engine, clipboard, hotkeys) to the app.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Velotext.Core.Input;
using Velotext.Core.Matching;
using Velotext.Core.Model;
using Velotext.Core.RichText;
using Velotext.Core.Settings;
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

        // Operations hold the keyboard at most this long; the paste timeout setting stays well below it.
        var options = new InputSequencerOptions { OperationTimeout = TimeSpan.FromSeconds(15) };
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

        _monitor?.Post(sequencer => sequencer.Enabled = !paused);
        LogPaused(_logger, paused);
        PausedChanged?.Invoke(this, paused);
    }

    /// <inheritdoc />
    public Phrase? FindPhrase(Guid id) => _library.Snapshot.Phrases.GetValueOrDefault(id);

    /// <inheritdoc />
    public void PhraseInserted(Guid id) => _usage.RecordUse(id);

    /// <inheritdoc />
    public void Notify(ExpansionNotice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        switch (notice.Kind)
        {
            case ExpansionNoticeKind.CopiedToClipboard:
                _notifications.Show("Copied to the clipboard", $"\"{notice.PhraseDescription}\" is on the clipboard. Press Ctrl+V to paste it.", NotificationKind.Info);
                break;
            case ExpansionNoticeKind.PasteNotConfirmed:
                _notifications.Show("Paste not confirmed", $"The app did not read \"{notice.PhraseDescription}\". If no text appeared, the app may block pasting.", NotificationKind.Warning);
                break;
            case ExpansionNoticeKind.ClipboardBusy:
                _notifications.Show("Clipboard in use", "Another app is using the clipboard. Velotext typed your trigger back. Try again.", NotificationKind.Warning);
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
        _monitor?.Dispose();
        _hotkeys?.Dispose();
        _engine?.Dispose();
        _clipboard?.Dispose();
    }

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

        _monitor?.Post(sequencer => sequencer.UndoEnabled = settings.UndoWithBackspace);
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
        var registrations = new List<HotkeyRegistration>();
        var seen = new HashSet<HotkeyGesture>();
        foreach (var phrase in library.EnumeratePhrases())
        {
            foreach (var hotkey in phrase.Triggers.OfType<HotkeyTrigger>())
            {
                if (hotkey.Gesture.ValidationError is null && seen.Add(hotkey.Gesture))
                {
                    registrations.Add(new HotkeyRegistration(phrase.Id, hotkey.Gesture));
                }
            }
        }

        if (_settings.Current.PauseHotkey is { } pause && seen.Add(pause))
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

        try
        {
            var failed = await _hotkeys.ReplaceAllAsync(registrations).ConfigureAwait(false);
            var pauseConflict = _settings.Current.PauseHotkey is { } p && failed.Contains(p);
            if (pauseConflict != PauseHotkeyConflict)
            {
                PauseHotkeyConflict = pauseConflict;
                PauseHotkeyConflictChanged?.Invoke(this, pauseConflict);
            }

            _library.ReportHotkeyConflicts([.. failed.Where(g => g != _settings.Current.PauseHotkey)]);
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
            SetPaused(!IsPaused);
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
