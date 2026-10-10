// Velotext: owns the phrase library: loading, validation, debounced crash-safe saving and change notification.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Collections.Frozen;
using Microsoft.Extensions.Logging;
using Velotext.Core.Model;
using Velotext.Core.Storage;
using Velotext.Core.Validation;

namespace Velotext.App.Services;

/// <summary>An immutable view of the library with a phrase lookup and its trigger problems.</summary>
/// <param name="Library">The library.</param>
/// <param name="Phrases">Phrases by id.</param>
/// <param name="Issues">Trigger problems, including hotkeys taken by other applications.</param>
public sealed record LibrarySnapshot(PhraseLibrary Library, IReadOnlyDictionary<Guid, Phrase> Phrases, IReadOnlyList<TriggerIssue> Issues);

/// <summary>A hotkey that cannot be used, with the reason shown to the user.</summary>
/// <param name="Gesture">The key combination.</param>
/// <param name="Message">Why it cannot be used and what to do.</param>
public sealed record HotkeyConflict(HotkeyGesture Gesture, string Message);

/// <summary>Whether the library on disk matches the library in memory.</summary>
public enum SaveState
{
    /// <summary>Everything is on disk.</summary>
    Saved = 0,

    /// <summary>A save is scheduled.</summary>
    Pending = 1,

    /// <summary>The last save did not work; changes are only in memory.</summary>
    Failed = 2,
}

/// <summary>
/// Owns the phrase library. The UI hands it a new immutable library after each edit; the service
/// validates it, announces it (the expansion engine picks up the new triggers at once) and saves it
/// half a second after the last edit.
/// </summary>
public sealed partial class LibraryService : IDisposable
{
    private readonly JsonFileStore<PhraseLibrary> _store;
    private readonly TimeProvider _time;
    private readonly ILogger<LibraryService> _logger;
    private readonly Debouncer _saver;
    private readonly Lock _gate = new();
    private readonly Lock _saveLock = new();
    private long _version;
    private LibrarySnapshot _snapshot = Create(PhraseLibrary.Empty, []);
    private IReadOnlyList<HotkeyConflict> _hotkeyConflicts = [];
    private SaveState _saveState;

    /// <summary>Creates the service.</summary>
    /// <param name="paths">File locations.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Diagnostics logger.</param>
    public LibraryService(AppPaths paths, TimeProvider time, ILogger<LibraryService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = new JsonFileStore<PhraseLibrary>(paths.LibraryFile, VelotextJsonContext.Default.PhraseLibrary, paths.BackupDirectory, time);
        _saver = new Debouncer(SaveNow, TimeSpan.FromMilliseconds(500));
    }

    /// <summary>Raised after the library changed, on the thread that changed it.</summary>
    public event EventHandler<LibrarySnapshot>? Changed;

    /// <summary>Raised when the list of problems changed without a library change (hotkey conflicts).</summary>
    public event EventHandler<LibrarySnapshot>? IssuesChanged;

    /// <summary>Raised when <see cref="SaveState"/> changes, on a background thread.</summary>
    public event EventHandler<SaveState>? SaveStateChanged;

    /// <summary>The current library.</summary>
    public LibrarySnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>How the library was loaded at start-up.</summary>
    public LoadStatus LoadStatus { get; private set; }

    /// <summary>Details of a recovery or failure at start-up (the backup used or the damaged file).</summary>
    public string? LoadDetail { get; private set; }

    /// <summary>Whether the library is on disk.</summary>
    public SaveState SaveState
    {
        get
        {
            lock (_gate)
            {
                return _saveState;
            }
        }
    }

    /// <summary>The folder with rolling backups.</summary>
    public string? BackupDirectory => _store.BackupDirectory;

    /// <summary>Loads the library. A first run gets the starter library; a damaged file falls back to the newest backup.</summary>
    /// <exception cref="LibraryUnavailableException">Another program keeps the library file locked.</exception>
    public void Load()
    {
        var result = _store.Load();
        if (result.Status == LoadStatus.Unavailable)
        {
            // Never start with an empty library that a later save would write over the real one.
            throw new LibraryUnavailableException(result.Detail ?? _store.Path);
        }

        LoadStatus = result.Status;
        LoadDetail = result.Detail;
        var library = result.Value ?? PhraseLibrary.CreateStarter(_time.GetUtcNow());
        if (result.Status != LoadStatus.Loaded)
        {
            LogLoad(_logger, result.Status, result.Detail ?? string.Empty);
        }

        library = LibrarySanitizer.Repair(library, _time.GetUtcNow());
        var snapshot = Publish(library);
        if (result.Status != LoadStatus.Loaded)
        {
            // Write the starter or recovered library so the next start is normal.
            _saver.Request();
        }

        Changed?.Invoke(this, snapshot);
    }

    /// <summary>Replaces the library after an edit, re-validates it and schedules a save.</summary>
    /// <param name="library">The edited library.</param>
    public void Update(PhraseLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);
        var snapshot = Publish(library);
        SetSaveState(SaveState.Pending);
        _saver.Request();
        Changed?.Invoke(this, snapshot);
    }

    /// <summary>Records hotkeys that cannot be used (owned by another app, or they type a character), so they show as problems.</summary>
    /// <param name="conflicts">The unusable hotkeys and why.</param>
    public void ReportHotkeyConflicts(IReadOnlyList<HotkeyConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        LibrarySnapshot snapshot;
        lock (_gate)
        {
            if (conflicts.SequenceEqual(_hotkeyConflicts))
            {
                return;
            }

            _hotkeyConflicts = conflicts;
            snapshot = _snapshot = Create(_snapshot.Library, conflicts);
        }

        IssuesChanged?.Invoke(this, snapshot);
    }

    /// <summary>Lists available backups, newest first.</summary>
    public IReadOnlyList<string> ListBackups() => _store.ListBackups();

    /// <summary>Saves pending edits, then copies the library file to the backup folder now.</summary>
    /// <returns>True when a backup was written.</returns>
    public bool BackupNow()
    {
        _saver.Flush();
        return _store.CreateBackup();
    }

    /// <summary>Reads a backup file without loading it.</summary>
    /// <param name="file">Backup file path.</param>
    public PhraseLibrary? ReadBackup(string file) => _store.TryRead(file);

    /// <summary>Saves pending changes now (for example before exit).</summary>
    public void Flush() => _saver.Flush();

    /// <summary>Saves pending changes and stops the save timer.</summary>
    public void Dispose() => _saver.Dispose();

    private static LibrarySnapshot Create(PhraseLibrary library, IReadOnlyList<HotkeyConflict> conflicts)
    {
        var issues = TriggerValidator.Validate(library).ToList();
        if (conflicts.Count > 0)
        {
            foreach (var phrase in library.EnumeratePhrases())
            {
                for (var position = 0; position < phrase.Triggers.Count; position++)
                {
                    if (phrase.Triggers[position] is HotkeyTrigger hotkey
                        && conflicts.FirstOrDefault(c => c.Gesture == hotkey.Gesture) is { } conflict
                        && !issues.Any(i => i.PhraseId == phrase.Id && i.TriggerPosition == position))
                    {
                        issues.Add(new TriggerIssue(phrase.Id, position, IssueSeverity.Error, conflict.Message));
                    }
                }
            }
        }

        var phrases = library.EnumeratePhrases().GroupBy(p => p.Id).ToFrozenDictionary(g => g.Key, g => g.First());
        return new LibrarySnapshot(library, phrases, issues);
    }

    private LibrarySnapshot Publish(PhraseLibrary library)
    {
        lock (_gate)
        {
            _snapshot = Create(library, _hotkeyConflicts);
            _version++;
            return _snapshot;
        }
    }

    private void SaveNow()
    {
        // The timer and Flush can save at the same time. Reading the snapshot inside the save lock makes
        // the newest library the last one written.
        lock (_saveLock)
        {
            PhraseLibrary library;
            long version;
            lock (_gate)
            {
                library = _snapshot.Library;
                version = _version;
            }

            try
            {
                _store.Save(library);

                // A newer edit arrived while saving: it has its own save scheduled, so stay "Saving".
                if (Interlocked.Read(ref _version) == version)
                {
                    SetSaveState(SaveState.Saved);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSaveFailed(_logger, ex);
                SetSaveState(SaveState.Failed);
            }
        }
    }

    private void SetSaveState(SaveState state)
    {
        lock (_gate)
        {
            if (_saveState == state)
            {
                return;
            }

            _saveState = state;
        }

        SaveStateChanged?.Invoke(this, state);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Library loaded with status {Status} ({Detail}).")]
    private static partial void LogLoad(ILogger logger, LoadStatus status, string detail);

    [LoggerMessage(Level = LogLevel.Error, Message = "The library could not be saved.")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);
}

/// <summary>Another program keeps the library file locked, so Velotext cannot read it safely.</summary>
public sealed class LibraryUnavailableException : IOException
{
    /// <summary>Creates the exception.</summary>
    public LibraryUnavailableException()
        : this("The library file is in use by another program.")
    {
    }

    /// <summary>Creates the exception for a file.</summary>
    /// <param name="message">The locked file or a message.</param>
    public LibraryUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a cause.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public LibraryUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
