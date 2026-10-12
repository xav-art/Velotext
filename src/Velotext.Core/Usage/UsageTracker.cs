// Velotext: tracks when each phrase was last inserted, and how often and how many characters it saved in 30 days.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace Velotext.Core.Usage;

/// <summary>When a phrase was last inserted, and how often and how many characters it saved on each recent day.</summary>
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

    /// <summary>Characters saved per UTC day (phrase characters minus trigger characters, summed). Never null.</summary>
    public IReadOnlyDictionary<DateOnly, int> Saved
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
    /// <summary>
    /// Schema version written by this build: 2 added per-day counts and dropped the all-time total; 3 added the
    /// characters saved per day.
    /// </summary>
    public const int CurrentSchemaVersion = 3;

    /// <summary>Schema version of the stored file.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Usage per phrase id. Never null: a file without it reads as empty.</summary>
    public IReadOnlyDictionary<Guid, PhraseUsage> Phrases
    {
        get;
        init => field = value ?? new Dictionary<Guid, PhraseUsage>();
    } = new Dictionary<Guid, PhraseUsage>();
}

/// <summary>Insertions and characters saved on one day of the window.</summary>
/// <param name="Day">The UTC day.</param>
/// <param name="Uses">Insertions on that day.</param>
/// <param name="CharactersSaved">Characters saved on that day.</param>
public sealed record DailyUsage(DateOnly Day, long Uses, long CharactersSaved);

/// <summary>Insertions and characters saved by one phrase in the window.</summary>
/// <param name="PhraseId">The phrase.</param>
/// <param name="Uses">Insertions in the window.</param>
/// <param name="CharactersSaved">Characters saved in the window.</param>
/// <param name="LastUsedUtc">Last insertion time.</param>
public sealed record PhraseTotals(Guid PhraseId, int Uses, long CharactersSaved, DateTimeOffset LastUsedUtc);

/// <summary>Usage in the window that ends today.</summary>
/// <param name="Days">Every day of the window, oldest first, including days without use.</param>
/// <param name="Phrases">Phrases used in the window, most used first.</param>
public sealed record UsageReport(IReadOnlyList<DailyUsage> Days, IReadOnlyList<PhraseTotals> Phrases)
{
    /// <summary>Insertions in the window.</summary>
    public long TotalUses => Days.Sum(d => d.Uses);

    /// <summary>Characters saved in the window.</summary>
    public long TotalCharactersSaved => Days.Sum(d => d.CharactersSaved);
}

/// <summary>
/// Thread-safe record of phrase insertions. It keeps the last use of each phrase and, per UTC day for the last
/// <see cref="WindowDays"/> days, the insertions and the characters saved; older days are deleted by <see cref="Trim"/>.
/// </summary>
public sealed class UsageTracker
{
    /// <summary>Number of days, today included, that usage counts cover.</summary>
    public const int WindowDays = 30;

    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Entry> _usage = [];

    /// <summary>Creates a tracker from stored data, dropping values below 1.</summary>
    /// <param name="data">Previously stored usage, or <c>null</c> for none.</param>
    /// <param name="savedPerUse">
    /// For files older than schema 3: the characters one use of a phrase saves today, to estimate the days stored
    /// without them. <c>null</c> leaves those days at zero.
    /// </param>
    public UsageTracker(UsageData? data = null, Func<Guid, int>? savedPerUse = null)
    {
        if (data is null)
        {
            return;
        }

        foreach (var (id, stored) in data.Phrases)
        {
            var entry = new Entry(stored.LastUsedUtc);
            CopyPositive(stored.Days, entry.Days);
            CopyPositive(stored.Saved, entry.Saved);

            // Schema 1 kept only an all-time total: it moves to the day of the last use.
            if (data.SchemaVersion < 2 && stored.LegacyUseCount is int total && total > 0)
            {
                entry.Days[DayOf(stored.LastUsedUtc)] = total;
            }

            // Before schema 3 nothing measured the characters saved: estimate them from today's text.
            if (data.SchemaVersion < 3 && savedPerUse is not null)
            {
                var perUse = Math.Max(0, savedPerUse(id));
                foreach (var (day, uses) in entry.Days)
                {
                    entry.Saved[day] = Clamp((long)uses * perUse);
                }
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
    /// <param name="charactersSaved">Characters the insertion saved (values below zero count as zero).</param>
    public void RecordUse(Guid phraseId, DateTimeOffset now, int charactersSaved = 0)
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
            entry.Days[day] = Clamp((long)entry.Days.GetValueOrDefault(day) + 1);
            if (charactersSaved > 0)
            {
                entry.Saved[day] = Clamp((long)entry.Saved.GetValueOrDefault(day) + charactersSaved);
            }
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
            return _usage.TryGetValue(phraseId, out var entry) ? Clamp(Sum(entry.Days, first, last)) : 0;
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

    /// <summary>Returns the phrases used most in the window, most used first, then most recent first.</summary>
    /// <param name="count">How many to return.</param>
    /// <param name="now">The current time.</param>
    /// <param name="exists">Filter that keeps only phrases that still exist.</param>
    public IReadOnlyList<Guid> MostUsed(int count, DateTimeOffset now, Func<Guid, bool> exists) =>
        [.. Report(now, exists).Phrases.Take(count).Select(p => p.PhraseId)];

    /// <summary>
    /// Returns the usage of the window that ends today: every day, and every phrase used in it (most used first,
    /// then most characters saved, then most recent). Phrases that no longer exist are left out.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="exists">Filter that keeps only phrases that still exist.</param>
    public UsageReport Report(DateTimeOffset now, Func<Guid, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        var (first, last) = Window(now);
        var uses = new long[WindowDays];
        var saved = new long[WindowDays];
        var phrases = new List<PhraseTotals>();
        lock (_gate)
        {
            foreach (var (id, entry) in _usage)
            {
                if (!exists(id))
                {
                    continue;
                }

                var phraseUses = Sum(entry.Days, first, last);
                if (phraseUses == 0)
                {
                    continue;
                }

                phrases.Add(new PhraseTotals(id, Clamp(phraseUses), Sum(entry.Saved, first, last), entry.LastUsedUtc));
                AddByDay(entry.Days, first, last, uses);
                AddByDay(entry.Saved, first, last, saved);
            }
        }

        var days = Enumerable.Range(0, WindowDays).Select(i => new DailyUsage(first.AddDays(i), uses[i], saved[i])).ToList();
        var ordered = phrases
            .OrderByDescending(p => p.Uses)
            .ThenByDescending(p => p.CharactersSaved)
            .ThenByDescending(p => p.LastUsedUtc)
            .ToList();
        return new UsageReport(days, ordered);
    }

    /// <summary>Deletes day values outside the window: older days, and days after today (a clock set back).</summary>
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
                removed |= RemoveOutside(entry.Days, first, last);
                removed |= RemoveOutside(entry.Saved, first, last);
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
                    u => new PhraseUsage
                    {
                        LastUsedUtc = u.Value.LastUsedUtc,
                        Days = new Dictionary<DateOnly, int>(u.Value.Days),
                        Saved = new Dictionary<DateOnly, int>(u.Value.Saved),
                    }),
            };
        }
    }

    private static (DateOnly First, DateOnly Last) Window(DateTimeOffset now)
    {
        var today = DayOf(now);
        return (today.AddDays(1 - WindowDays), today);
    }

    private static int Clamp(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    private static void CopyPositive(IReadOnlyDictionary<DateOnly, int> from, Dictionary<DateOnly, int> to)
    {
        foreach (var (day, value) in from)
        {
            if (value > 0)
            {
                to[day] = value;
            }
        }
    }

    private static long Sum(Dictionary<DateOnly, int> values, DateOnly first, DateOnly last)
    {
        long total = 0;
        foreach (var (day, value) in values)
        {
            if (day >= first && day <= last)
            {
                total += value;
            }
        }

        return total;
    }

    private static void AddByDay(Dictionary<DateOnly, int> values, DateOnly first, DateOnly last, long[] totals)
    {
        foreach (var (day, value) in values)
        {
            if (day >= first && day <= last)
            {
                totals[day.DayNumber - first.DayNumber] += value;
            }
        }
    }

    private static bool RemoveOutside(Dictionary<DateOnly, int> values, DateOnly first, DateOnly last)
    {
        var outside = values.Keys.Where(d => d < first || d > last).ToList();
        foreach (var day in outside)
        {
            values.Remove(day);
        }

        return outside.Count > 0;
    }

    /// <summary>Mutable usage of one phrase, guarded by the tracker's lock.</summary>
    /// <param name="lastUsedUtc">Last insertion time.</param>
    private sealed class Entry(DateTimeOffset lastUsedUtc)
    {
        public DateTimeOffset LastUsedUtc { get; set; } = lastUsedUtc;

        public Dictionary<DateOnly, int> Days { get; } = [];

        public Dictionary<DateOnly, int> Saved { get; } = [];
    }
}
