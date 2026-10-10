// Velotext: crash-safe JSON file storage with rolling backups and corrupt-file recovery.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Velotext.Core.Storage;

/// <summary>How a stored file was loaded.</summary>
public enum LoadStatus
{
    /// <summary>No file yet (first run).</summary>
    Missing = 0,

    /// <summary>Loaded normally.</summary>
    Loaded = 1,

    /// <summary>The file was damaged; a backup was loaded instead and the damaged file was set aside.</summary>
    Recovered = 2,

    /// <summary>The file was damaged and no usable backup exists; the damaged file was set aside.</summary>
    Failed = 3,
}

/// <summary>Result of loading a stored file.</summary>
/// <typeparam name="T">Stored type.</typeparam>
/// <param name="Value">The loaded value, or <c>null</c> when missing or failed.</param>
/// <param name="Status">How it was loaded.</param>
/// <param name="Detail">For <see cref="LoadStatus.Recovered"/> the backup used; for failures the set-aside file.</param>
public sealed record LoadResult<T>(T? Value, LoadStatus Status, string? Detail = null)
    where T : class;

/// <summary>
/// Stores one value as a JSON file. Writes go to a temporary file first and then replace the target
/// in one step, so a crash or power loss never leaves a half-written file. Before the first save of a
/// session (and then at most once per <see cref="BackupInterval"/>) the current file is copied to the
/// backup folder; the newest <see cref="BackupsToKeep"/> copies are kept.
/// </summary>
/// <typeparam name="T">Stored type.</typeparam>
public sealed class JsonFileStore<T>
    where T : class
{
    private readonly JsonTypeInfo<T> _typeInfo;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private DateTimeOffset? _lastBackup;

    /// <summary>Creates a store.</summary>
    /// <param name="path">Full path of the JSON file.</param>
    /// <param name="typeInfo">Source-generated metadata for <typeparamref name="T"/>.</param>
    /// <param name="backupDirectory">Folder for rolling backups, or <c>null</c> to keep none.</param>
    /// <param name="time">Clock used for backup names and intervals.</param>
    public JsonFileStore(string path, JsonTypeInfo<T> typeInfo, string? backupDirectory, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        _typeInfo = typeInfo ?? throw new ArgumentNullException(nameof(typeInfo));
        BackupDirectory = backupDirectory is null ? null : System.IO.Path.GetFullPath(backupDirectory);
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Full path of the stored file.</summary>
    public string Path { get; }

    /// <summary>Folder for rolling backups, or <c>null</c>.</summary>
    public string? BackupDirectory { get; }

    /// <summary>Number of rolling backups kept.</summary>
    public int BackupsToKeep { get; init; } = 20;

    /// <summary>Minimum time between two rolling backups within one session.</summary>
    public TimeSpan BackupInterval { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Loads the file, falling back to the newest readable backup when it is damaged.</summary>
    public LoadResult<T> Load()
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
            {
                // A crash between the two steps of a first save can leave only the .bak behind.
                var orphan = TryRead(Path + ".bak");
                return orphan is null ? new LoadResult<T>(null, LoadStatus.Missing) : new LoadResult<T>(orphan, LoadStatus.Recovered, Path + ".bak");
            }

            var value = TryRead(Path);
            if (value is not null)
            {
                return new LoadResult<T>(value, LoadStatus.Loaded);
            }

            var setAside = SetAsideDamagedFile();
            foreach (var candidate in BackupCandidates())
            {
                var recovered = TryRead(candidate);
                if (recovered is not null)
                {
                    return new LoadResult<T>(recovered, LoadStatus.Recovered, candidate);
                }
            }

            return new LoadResult<T>(null, LoadStatus.Failed, setAside);
        }
    }

    /// <summary>Saves the value atomically.</summary>
    /// <param name="value">The value to store.</param>
    public void Save(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, _typeInfo);
        lock (_gate)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            BackUpIfDue();

            var temp = Path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            Retry(() =>
            {
                if (File.Exists(Path))
                {
                    File.Replace(temp, Path, Path + ".bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temp, Path);
                }
            });
        }
    }

    /// <summary>Copies the current file to the backup folder now, for example before replacing the whole library.</summary>
    /// <returns>True when a backup was written.</returns>
    public bool CreateBackup()
    {
        lock (_gate)
        {
            _lastBackup = null;
            BackUpIfDue();
            return _lastBackup is not null;
        }
    }

    /// <summary>Lists available backups, newest first.</summary>
    public IReadOnlyList<string> ListBackups()
    {
        if (BackupDirectory is null || !Directory.Exists(BackupDirectory))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(BackupDirectory, BackupPrefix + "*.json").OrderByDescending(f => f, StringComparer.Ordinal)];
    }

    /// <summary>Reads a backup or any other file of the same type, returning <c>null</c> when it is unreadable.</summary>
    /// <param name="file">The file to read.</param>
    public T? TryRead(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(stream, _typeInfo);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private string BackupPrefix => System.IO.Path.GetFileNameWithoutExtension(Path) + "-";

    private IEnumerable<string> BackupCandidates()
    {
        yield return Path + ".bak";
        foreach (var backup in ListBackups())
        {
            yield return backup;
        }
    }

    private string SetAsideDamagedFile()
    {
        var target = string.Create(CultureInfo.InvariantCulture, $"{Path}.damaged-{_time.GetUtcNow():yyyyMMdd-HHmmss}");
        try
        {
            File.Move(Path, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (IsFileAccessError(ex))
        {
            return Path;
        }
    }

    private void BackUpIfDue()
    {
        var now = _time.GetUtcNow();
        if (BackupDirectory is null || !File.Exists(Path) || (_lastBackup is { } last && now - last < BackupInterval))
        {
            return;
        }

        // Backups are best effort: a locked file must never stop the save itself.
        try
        {
            Directory.CreateDirectory(BackupDirectory);
            var name = string.Create(CultureInfo.InvariantCulture, $"{BackupPrefix}{now:yyyyMMdd-HHmmss-fff}.json");
            File.Copy(Path, System.IO.Path.Combine(BackupDirectory, name), overwrite: true);
            _lastBackup = now;
        }
        catch (Exception ex) when (IsFileAccessError(ex))
        {
            return;
        }

        foreach (var old in ListBackups().Skip(BackupsToKeep))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (IsFileAccessError(ex))
            {
                // A locked or read-only old backup is harmless; it is retried on the next backup.
            }
        }
    }

    private static void Retry(Action action)
    {
        // Antivirus and sync tools briefly lock freshly written files; a short retry rides that out.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (IsFileAccessError(ex) && attempt < 5)
            {
                Thread.Sleep(50 * attempt);
            }
        }
    }

    private static bool IsFileAccessError(Exception ex) => ex is IOException or UnauthorizedAccessException;
}
