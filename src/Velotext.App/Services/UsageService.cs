// Velotext: records which phrases were inserted and when, for the "Recently used" folder.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Velotext.Core.Storage;
using Velotext.Core.Usage;

namespace Velotext.App.Services;

/// <summary>Wraps the usage tracker with loading and a debounced save to usage.json.</summary>
public sealed partial class UsageService : IDisposable
{
    private readonly JsonFileStore<UsageData> _store;
    private readonly TimeProvider _time;
    private readonly ILogger<UsageService> _logger;
    private readonly Debouncer _saver;

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
    }

    /// <summary>Raised after a phrase was used, on a background thread.</summary>
    public event EventHandler? Changed;

    /// <summary>The tracker with the current data.</summary>
    public UsageTracker Tracker { get; private set; }

    /// <summary>Loads usage.json; a missing or damaged file starts empty.</summary>
    public void Load()
    {
        Tracker = new UsageTracker(_store.Load().Value);
        Tracker.Changed += (_, _) =>
        {
            _saver.Request();
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Records one use of a phrase now.</summary>
    /// <param name="phraseId">The phrase.</param>
    public void RecordUse(Guid phraseId) => Tracker.RecordUse(phraseId, _time.GetUtcNow());

    /// <summary>Saves pending changes and stops the timer.</summary>
    public void Dispose() => _saver.Dispose();

    private void Save()
    {
        try
        {
            _store.Save(Tracker.Snapshot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Usage data could not be saved.")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);
}
