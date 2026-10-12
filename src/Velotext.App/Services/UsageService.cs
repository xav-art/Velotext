// Velotext: records which phrases were inserted, when, and the characters they saved, for "Recently used", "Most used" and the dashboard.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Velotext.Core.Storage;
using Velotext.Core.Usage;

namespace Velotext.App.Services;

/// <summary>
/// Wraps the usage tracker with loading and a debounced save to usage.json. Day counts older than the
/// 30-day window are deleted on load, before every save, and by an hourly check while the app runs.
/// </summary>
public sealed partial class UsageService : IDisposable
{
    private readonly JsonFileStore<UsageData> _store;
    private readonly TimeProvider _time;
    private readonly ILogger<UsageService> _logger;
    private readonly Debouncer _saver;
    private readonly ITimer _trimTimer;
    private bool _saveBlocked;

    /// <summary>Creates the service.</summary>
    /// <param name="paths">File locations.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Diagnostics logger.</param>
    public UsageService(AppPaths paths, TimeProvider time, ILogger<UsageService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = new JsonFileStore<UsageData>(paths.UsageFile, VelotextJsonContext.Default.UsageData, backupDirectory: null, time);
        _saver = new Debouncer(Save, TimeSpan.FromSeconds(2));
        Tracker = new UsageTracker();
        _trimTimer = time.CreateTimer(_ => TrimExpired(), null, TrimInterval, TrimInterval);
    }

    /// <summary>How often old day counts are deleted while the app runs.</summary>
    public static TimeSpan TrimInterval { get; } = TimeSpan.FromHours(1);

    /// <summary>Raised after a phrase was used or old counts were deleted, on a background thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The tracker with the current data.</summary>
    public UsageTracker Tracker { get; private set; }

    /// <summary>Loads usage.json; a missing or damaged file starts empty.</summary>
    /// <param name="savedPerUse">
    /// The characters one use of a phrase saves today, to estimate days stored before the characters saved were
    /// measured; <c>null</c> leaves them at zero.
    /// </param>
    public void Load(Func<Guid, int>? savedPerUse = null)
    {
        var result = _store.Load();

        // A file that another program keeps locked holds real history: never write over it in this session.
        _saveBlocked = result.Status == LoadStatus.Unavailable;
        if (_saveBlocked)
        {
            LogUsageUnavailable(_logger);
        }

        Tracker = new UsageTracker(result.Value, savedPerUse);
        Tracker.Changed += (_, _) =>
        {
            _saver.Request();
            Changed?.Invoke(this, EventArgs.Empty);
        };

        // Rewrite the file at once when it holds expired days or the older schema with the all-time total.
        var expired = Tracker.Trim(_time.GetUtcNow());
        if (expired || result.Value is { SchemaVersion: < UsageData.CurrentSchemaVersion })
        {
            _saver.Request();
        }
    }

    /// <summary>Returns how often a phrase was inserted in the last 30 days.</summary>
    /// <param name="phraseId">The phrase.</param>
    public int UsesInWindow(Guid phraseId) => Tracker.UsesInWindow(phraseId, _time.GetUtcNow());

    /// <summary>Returns the phrases used most in the last 30 days, most used first.</summary>
    /// <param name="count">How many to return.</param>
    /// <param name="exists">Filter that keeps only phrases that still exist.</param>
    public IReadOnlyList<Guid> MostUsed(int count, Func<Guid, bool> exists) => Tracker.MostUsed(count, _time.GetUtcNow(), exists);

    /// <summary>Deletes day counts that left the window; saves and raises <see cref="Changed"/> when any did.</summary>
    public void TrimExpired()
    {
        if (Tracker.Trim(_time.GetUtcNow()))
        {
            _saver.Request();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Records one use of a phrase now.</summary>
    /// <param name="phraseId">The phrase.</param>
    /// <param name="charactersSaved">Characters the use saved.</param>
    public void RecordUse(Guid phraseId, int charactersSaved = 0) => Tracker.RecordUse(phraseId, _time.GetUtcNow(), charactersSaved);

    /// <summary>Returns the usage of the last 30 days for the dashboard.</summary>
    /// <param name="exists">Filter that keeps only phrases that still exist.</param>
    public UsageReport Report(Func<Guid, bool> exists) => Tracker.Report(_time.GetUtcNow(), exists);

    /// <summary>Saves pending changes and stops the timers.</summary>
    public void Dispose()
    {
        _trimTimer.Dispose();
        _saver.Dispose();
    }

    private void Save()
    {
        if (_saveBlocked)
        {
            return;
        }

        try
        {
            Tracker.Trim(_time.GetUtcNow());
            _store.Save(Tracker.Snapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The usage file is locked by another program; usage from this session is not saved.")]
    private static partial void LogUsageUnavailable(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Usage data could not be saved.")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);
}
