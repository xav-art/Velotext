// Velotext: loads, changes and saves the user's settings.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Velotext.Core.Settings;
using Velotext.Core.Storage;

namespace Velotext.App.Services;

/// <summary>Holds the current settings. Changes are saved at once and announced through <see cref="Changed"/>.</summary>
public sealed partial class SettingsService
{
    private readonly JsonFileStore<AppSettings> _store;
    private readonly ILogger<SettingsService> _logger;
    private readonly Lock _gate = new();
    private AppSettings _current = new();
    private bool _unavailable;

    /// <summary>Creates the service.</summary>
    /// <param name="paths">File locations.</param>
    /// <param name="logger">Diagnostics logger.</param>
    public SettingsService(AppPaths paths, ILogger<SettingsService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _store = new JsonFileStore<AppSettings>(paths.SettingsFile, VelotextJsonContext.Default.AppSettings, backupDirectory: null);
    }

    /// <summary>Raised after the settings changed, on the thread that changed them.</summary>
    public event EventHandler<AppSettings>? Changed;

    /// <summary>True when no settings file existed (first run).</summary>
    public bool IsFirstRun { get; private set; }

    /// <summary>The current settings.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>Loads the settings file; damaged or missing files give defaults.</summary>
    public void Load()
    {
        var result = _store.Load();
        IsFirstRun = result.Status == LoadStatus.Missing;
        if (result.Status is LoadStatus.Failed or LoadStatus.Recovered or LoadStatus.Unavailable)
        {
            LogSettingsRecovered(_logger, result.Status);
        }

        lock (_gate)
        {
            _current = (result.Value ?? new AppSettings()).Normalize();
            _unavailable = result.Status == LoadStatus.Unavailable;
        }
    }

    /// <summary>Applies a change, saves it and raises <see cref="Changed"/>.</summary>
    /// <param name="change">Returns the new settings from the current ones.</param>
    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings next;
        bool save;
        lock (_gate)
        {
            // The file was locked at startup: read it now, so the user's real settings are the base of
            // the change instead of the defaults.
            if (_unavailable && _store.Load() is { Status: not LoadStatus.Unavailable } retry)
            {
                _unavailable = false;
                _current = (retry.Value ?? new AppSettings()).Normalize();
            }

            next = change(_current).Normalize();
            if (next == _current)
            {
                return;
            }

            _current = next;
            save = !_unavailable;
        }

        if (save)
        {
            try
            {
                _store.Save(next);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogSaveFailed(_logger, ex);
            }
        }
        else
        {
            // Never write defaults over settings that another program keeps locked.
            LogSaveSkipped(_logger);
        }

        Changed?.Invoke(this, next);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings file could not be read normally ({Status}); defaults or a backup are in use.")]
    private static partial void LogSettingsRecovered(ILogger logger, LoadStatus status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The settings file is still locked by another program; the change applies until Velotext closes but is not saved.")]
    private static partial void LogSaveSkipped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Settings could not be saved.")]
    private static partial void LogSaveFailed(ILogger logger, Exception exception);
}
