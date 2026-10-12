// Velotext: tests for usage tracking (recent order, 30-day counts, most used, trimming, schema 1 migration).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Usage;

namespace Velotext.Core.Tests.Usage;

/// <summary>Behaviour of <see cref="UsageTracker"/>: last use, per-day counts in the 30-day window and stored data.</summary>
public sealed class UsageTrackerTests
{
    private static readonly DateOnly s_today = UsageTracker.DayOf(Build.Now);

    private static UsageData Stored(int schemaVersion, Guid id, PhraseUsage usage) =>
        new() { SchemaVersion = schemaVersion, Phrases = new Dictionary<Guid, PhraseUsage> { [id] = usage } };

    [Fact]
    public void Orders_by_recency_and_prunes_deleted_phrases()
    {
        var tracker = new UsageTracker();
        var changes = 0;
        tracker.Changed += (_, _) => changes++;
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var gone = Guid.NewGuid();

        tracker.RecordUse(a, Build.Now);
        tracker.RecordUse(b, Build.Now.AddMinutes(1));
        tracker.RecordUse(gone, Build.Now.AddMinutes(2));
        tracker.RecordUse(a, Build.Now.AddMinutes(3));

        Assert.Equal([a, b], tracker.MostRecent(5, id => id != gone));
        Assert.Equal(4, changes);

        tracker.Prune(new HashSet<Guid> { a });
        var snapshot = tracker.Snapshot();
        var usage = Assert.Single(snapshot.Phrases).Value;
        Assert.Equal(Build.Now.AddMinutes(3), usage.LastUsedUtc);
        Assert.Equal(2, usage.Days[s_today]);
        Assert.Null(usage.LegacyUseCount);
        Assert.Equal(UsageData.CurrentSchemaVersion, snapshot.SchemaVersion);

        var restored = new UsageTracker(snapshot);
        Assert.Equal([a], restored.MostRecent(1, _ => true));
        Assert.Equal(2, restored.UsesInWindow(a, Build.Now));
    }

    [Fact]
    public void Counts_cover_today_and_the_29_days_before()
    {
        var tracker = new UsageTracker();
        var id = Guid.NewGuid();

        tracker.RecordUse(id, Build.Now.AddDays(-30));
        tracker.RecordUse(id, Build.Now.AddDays(-29));
        tracker.RecordUse(id, Build.Now.AddDays(-1));
        tracker.RecordUse(id, Build.Now);
        tracker.RecordUse(id, Build.Now.AddHours(1));

        Assert.Equal(4, tracker.UsesInWindow(id, Build.Now));
        Assert.Equal(3, tracker.UsesInWindow(id, Build.Now.AddDays(1)));
        Assert.Equal(0, tracker.UsesInWindow(id, Build.Now.AddDays(30)));
        Assert.Equal(0, tracker.UsesInWindow(Guid.NewGuid(), Build.Now));
        Assert.Equal(30, UsageTracker.WindowDays);
    }

    [Fact]
    public void Day_is_the_utc_day()
    {
        var lateEvening = new DateTimeOffset(2026, 10, 10, 23, 30, 0, TimeSpan.FromHours(-5));

        Assert.Equal(new DateOnly(2026, 10, 11), UsageTracker.DayOf(lateEvening));
    }

    [Fact]
    public void Most_used_ranks_by_uses_then_by_last_use_and_skips_unused_and_deleted()
    {
        var tracker = new UsageTracker();
        var busy = Guid.NewGuid();
        var olderTie = Guid.NewGuid();
        var newerTie = Guid.NewGuid();
        var expired = Guid.NewGuid();
        var deleted = Guid.NewGuid();

        tracker.RecordUse(expired, Build.Now.AddDays(-40));
        tracker.RecordUse(olderTie, Build.Now.AddMinutes(1));
        tracker.RecordUse(newerTie, Build.Now.AddMinutes(2));
        for (var i = 0; i < 3; i++)
        {
            tracker.RecordUse(busy, Build.Now);
            tracker.RecordUse(deleted, Build.Now);
        }

        Assert.Equal([busy, newerTie, olderTie], tracker.MostUsed(10, Build.Now.AddMinutes(5), id => id != deleted));
        Assert.Equal([busy], tracker.MostUsed(1, Build.Now, id => id != deleted));
    }

    [Fact]
    public void Trim_deletes_days_outside_the_window_and_reports_it()
    {
        var id = Guid.NewGuid();
        var tracker = new UsageTracker(Stored(2, id, new PhraseUsage
        {
            LastUsedUtc = Build.Now,
            Days = new Dictionary<DateOnly, int>
            {
                [s_today.AddDays(-30)] = 5,
                [s_today.AddDays(-29)] = 2,
                [s_today] = 1,
                [s_today.AddDays(1)] = 7,
            },
        }));

        Assert.Equal(3, tracker.UsesInWindow(id, Build.Now));
        Assert.True(tracker.Trim(Build.Now));
        Assert.False(tracker.Trim(Build.Now));

        var days = tracker.Snapshot().Phrases[id].Days;
        Assert.Equal([s_today.AddDays(-29), s_today], days.Keys.Order());

        // The last use stays for "Recently used" after its counts expire.
        Assert.True(tracker.Trim(Build.Now.AddDays(60)));
        Assert.Empty(tracker.Snapshot().Phrases[id].Days);
        Assert.Equal([id], tracker.MostRecent(5, _ => true));
        Assert.Empty(tracker.MostUsed(5, Build.Now.AddDays(60), _ => true));
    }

    [Fact]
    public void Stored_counts_below_one_are_dropped_and_counts_never_overflow()
    {
        var id = Guid.NewGuid();
        var tracker = new UsageTracker(Stored(2, id, new PhraseUsage
        {
            LastUsedUtc = Build.Now,
            Days = new Dictionary<DateOnly, int>
            {
                [s_today.AddDays(-2)] = 0,
                [s_today.AddDays(-1)] = int.MaxValue,
                [s_today] = int.MaxValue,
            },
        }));

        tracker.RecordUse(id, Build.Now);

        Assert.Equal(int.MaxValue, tracker.Snapshot().Phrases[id].Days[s_today]);
        Assert.Equal(int.MaxValue, tracker.UsesInWindow(id, Build.Now));
        Assert.DoesNotContain(s_today.AddDays(-2), tracker.Snapshot().Phrases[id].Days.Keys);
    }

    [Fact]
    public void Schema_1_total_moves_to_the_day_of_the_last_use()
    {
        var recent = Guid.NewGuid();
        var old = Guid.NewGuid();
        var none = Guid.NewGuid();
        var data = new UsageData
        {
            SchemaVersion = 1,
            Phrases = new Dictionary<Guid, PhraseUsage>
            {
                [recent] = new() { LastUsedUtc = Build.Now.AddDays(-2), LegacyUseCount = 7 },
                [old] = new() { LastUsedUtc = Build.Now.AddDays(-45), LegacyUseCount = 3 },
                [none] = new() { LastUsedUtc = Build.Now.AddDays(-1), LegacyUseCount = 0 },
            },
        };

        var tracker = new UsageTracker(data);

        Assert.Equal(7, tracker.UsesInWindow(recent, Build.Now));
        Assert.Equal(0, tracker.UsesInWindow(old, Build.Now));
        Assert.Equal(0, tracker.UsesInWindow(none, Build.Now));
        Assert.Equal([recent], tracker.MostUsed(10, Build.Now, _ => true));
        Assert.True(tracker.Trim(Build.Now));
        Assert.All(tracker.Snapshot().Phrases.Values, usage => Assert.Null(usage.LegacyUseCount));
    }

    [Fact]
    public void Schema_2_ignores_a_stray_total()
    {
        var id = Guid.NewGuid();

        var tracker = new UsageTracker(Stored(2, id, new PhraseUsage { LastUsedUtc = Build.Now, LegacyUseCount = 9 }));

        Assert.Equal(0, tracker.UsesInWindow(id, Build.Now));
    }

    [Fact]
    public void Arguments_are_checked()
    {
        var tracker = new UsageTracker();

        Assert.Throws<ArgumentNullException>(() => tracker.MostRecent(1, null!));
        Assert.Throws<ArgumentNullException>(() => tracker.MostUsed(1, Build.Now, null!));
        Assert.Throws<ArgumentNullException>(() => tracker.Prune(null!));
    }
}
