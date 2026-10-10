// Velotext: tracks when each phrase was last inserted, for the "Recently used" folder.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

namespace Velotext.Core.Usage;

/// <summary>How often and when a phrase was inserted.</summary>
/// <param name="LastUsedUtc">Last insertion time (UTC).</param>
/// <param name="UseCount">Total number of insertions.</param>
public sealed record PhraseUsage(DateTimeOffset LastUsedUtc, int UseCount);

/// <summary>Stored usage data (usage.json). Kept apart from the library so inserting never rewrites phrases.</summary>
public sealed record UsageData
{
    /// <summary>Schema version written by this build.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Schema version of the stored file.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Usage per phrase id.</summary>
    public IReadOnlyDictionary<Guid, PhraseUsage> Phrases { get; init; } = new Dictionary<Guid, PhraseUsage>();
}

/// <summary>Thread-safe record of phrase insertions.</summary>
public sealed class UsageTracker
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, PhraseUsage> _usage;

    /// <summary>Creates a tracker from stored data.</summary>
    /// <param name="data">Previously stored usage, or <c>null</c> for none.</param>
    public UsageTracker(UsageData? data = null)
    {
        _usage = data?.Phrases is { } phrases ? new Dictionary<Guid, PhraseUsage>(phrases) : [];
    }

    /// <summary>Raised after a use is recorded, on the recording thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Records one insertion of a phrase.</summary>
    /// <param name="phraseId">The inserted phrase.</param>
    /// <param name="now">Insertion time.</param>
    public void RecordUse(Guid phraseId, DateTimeOffset now)
    {
        lock (_gate)
        {
            var count = _usage.TryGetValue(phraseId, out var existing) ? existing.UseCount : 0;
            _usage[phraseId] = new PhraseUsage(now, count == int.MaxValue ? count : count + 1);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns the most recently used phrase ids, newest first.</summary>
    /// <param name="count">How many to return.</param>
    /// <param name="exists">Filter that keeps only phrases that still exist.</param>
    public IReadOnlyList<Guid> MostRecent(int count, Func<Guid, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        lock (_gate)
        {
            return [.. _usage.Where(u => exists(u.Key)).OrderByDescending(u => u.Value.LastUsedUtc).Take(count).Select(u => u.Key)];
        }
    }

    /// <summary>Drops usage of phrases that no longer exist.</summary>
    /// <param name="existing">Ids of phrases that still exist.</param>
    public void Prune(IReadOnlySet<Guid> existing)
    {
        ArgumentNullException.ThrowIfNull(existing);
        lock (_gate)
        {
            foreach (var id in _usage.Keys.Where(id => !existing.Contains(id)).ToList())
            {
                _usage.Remove(id);
            }
        }
    }

    /// <summary>Returns a copy of the current data for saving.</summary>
    public UsageData Snapshot()
    {
        lock (_gate)
        {
            return new UsageData { Phrases = new Dictionary<Guid, PhraseUsage>(_usage) };
        }
    }
}
