// Velotext: tracks when each phrase was last inserted and how often in the last 30 days.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Velotext.Core.Usage;

/// <summary>When a phrase was last inserted and how often on each recent day.</summary>
public sealed record PhraseUsage
{
    /// <summary>Last insertion time (UTC), for the "Recently used" folder.</summary>
    public DateTimeOffset LastUsedUtc { get; init; }

    /// <summary>Insertions per UTC day. Only days inside the usage window are kept. Never null: a file without it reads as empty.</summary>
    public IReadOnlyDictionary<DateOnly, int> Days
    {
        get;
        init => field = value ?? new Dictionary<DateOnly, int>();
    } = new Dictionary<DateOnly, int>();

    /// <summary>
    /// All-time total written by schema 1 files. Read once to seed <see cref="Days"/>, never written again.
    /// </summary>
    [JsonPropertyName("useCount")]
    public int? LegacyUseCount { get; init; }
}

/// <summary>Stored usage data (usage.json). Kept apart from the library so inserting never rewrites phrases.</summary>
public sealed record UsageData
{
    /// <summary>Schema version written by this build: 2 added per-day counts and dropped the all-time total.</summary>
    public const int CurrentSchemaVersion = 2;

    /// <summary>Schema version of the stored file.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Usage per phrase id. Never null: a file without it reads as empty.</summary>
    public IReadOnlyDictionary<Guid, PhraseUsage> Phrases
    {
        get;
        init => field = value ?? new Dictionary<Guid, PhraseUsage>();
    } = new Dictionary<Guid, PhraseUsage>();
}

/// <summary>
/// Thread-safe record of phrase insertions. It keeps the last use of each phrase and one count per UTC
/// day for the last <see cref="WindowDays"/> days; older days are deleted by <see cref="Trim"/>.
/// </summary>
public sealed class UsageTracker
{
    /// <summary>Number of days, today included, that usage counts cover.</summary>
    public const int WindowDays = 30;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _usage = [];

    /// <summary>Creates a tracker from stored data, dropping counts below 1.</summary>
    /// <param name="data">Previously stored usage, or <c>null</c> for none.</param>
    public UsageTracker(UsageData? data = null)
    {
        if (data is null)
        {
            return;
        }

        var legacy = data.SchemaVersion < 2;
        foreach (var (id, stored) in data.Phrases)
        {
            var entry = new Entry(stored.LastUsedUtc);
            foreach (var (day, count) in stored.Days)
            {
                if (count > 0)
                {
                    entry.Days[day] = count;
                }
            }

            // Schema 1 kept only an all-time total: it moves to the day of the last use.
            if (legacy && stored.LegacyUseCount is int total && total > 0)
            {
                entry.Days[DayOf(stored.LastUsedUtc)] = total;
            }

            _usage[id] = entry;
        }
    }

    /// <summary>Raised after a use is recorded, on the recording thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Returns the UTC day that a moment falls on.</summary>
    /// <param name="moment">The moment.</param>
    public static DateOnly DayOf(DateTimeOffset moment) => DateOnly.FromDateTime(moment.UtcDateTime);

    /// <summary>Records one insertion of a phrase.</summary>
    /// <param name="phraseId">The inserted phrase.</param>
    /// <param name="now">Insertion time.</param>
    public void RecordUse(Guid phraseId, DateTimeOffset now)
    {
        var day = DayOf(now);
        lock (_gate)
        {
            if (!_usage.TryGetValue(phraseId, out var entry))
            {
                entry = new Entry(now);
                _usage[phraseId] = entry;
            }

            entry.LastUsedUtc = now;
            var count = entry.Days.GetValueOrDefault(day);
            entry.Days[day] = count == int.MaxValue ? count : count + 1;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Returns how often a phrase was inserted in the window that ends today.</summary>
    /// <param name="phraseId">The phrase.</param>
    /// <param name="now">The current time.</param>
    public int UsesInWindow(Guid phraseId, DateTimeOffset now)
    {
        var (first, last) = Window(now);
        lock (_gate)
        {
            return _usage.TryGetValue(phraseId, out var entry) ? Sum(entry, first, last) : 0;
        }
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

    /// <summary>Returns the phrase ids used most in the window, most used first, then most recent first.</summary>
    /// <param name="count">How many to return.</param>
    /// <param name="now">The current time.</param>
    /// <param name="exists">Filter that keeps only phrases that still exist.</param>
    public IReadOnlyList<Guid> MostUsed(int count, DateTimeOffset now, Func<Guid, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        var (first, last) = Window(now);
        lock (_gate)
        {
            return
            [
                .. _usage
                    .Where(u => exists(u.Key))
                    .Select(u => (u.Key, Uses: Sum(u.Value, first, last), u.Value.LastUsedUtc))
                    .Where(u => u.Uses > 0)
                    .OrderByDescending(u => u.Uses)
                    .ThenByDescending(u => u.LastUsedUtc)
                    .Take(count)
                    .Select(u => u.Key),
            ];
        }
    }

    /// <summary>Deletes day counts outside the window: older days, and days after today (a clock set back).</summary>
    /// <param name="now">The current time.</param>
    /// <returns>True when anything was deleted.</returns>
    public bool Trim(DateTimeOffset now)
    {
        var (first, last) = Window(now);
        var removed = false;
        lock (_gate)
        {
            foreach (var entry in _usage.Values)
            {
                foreach (var day in entry.Days.Keys.Where(d => d < first || d > last).ToList())
                {
                    entry.Days.Remove(day);
                    removed = true;
                }
            }
        }

        return removed;
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
            return new UsageData
            {
                Phrases = _usage.ToDictionary(
                    u => u.Key,
                    u => new PhraseUsage { LastUsedUtc = u.Value.LastUsedUtc, Days = new Dictionary<DateOnly, int>(u.Value.Days) }),
            };
        }
    }

    private static (DateOnly First, DateOnly Last) Window(DateTimeOffset now)
    {
        var today = DayOf(now);
        return (today.AddDays(1 - WindowDays), today);
    }

    private static int Sum(Entry entry, DateOnly first, DateOnly last)
    {
        long total = 0;
        foreach (var (day, count) in entry.Days)
        {
            if (day >= first && day <= last)
            {
                total += count;
            }
        }

        return (int)Math.Min(total, int.MaxValue);
    }

    /// <summary>Mutable usage of one phrase, guarded by the tracker's lock.</summary>
    /// <param name="lastUsedUtc">Last insertion time.</param>
    private sealed class Entry(DateTimeOffset lastUsedUtc)
    {
        public DateTimeOffset LastUsedUtc { get; set; } = lastUsedUtc;

        public Dictionary<DateOnly, int> Days { get; } = [];
    }
}
