// Velotext: the usage dashboard shown when "Most used" is selected (totals, time saved, daily chart, phrase table).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Velotext.Core.Usage;

namespace Velotext.App.ViewModels;

/// <summary>One bar of the daily chart.</summary>
/// <param name="Height">Bar height in device-independent pixels (0 for a day without use).</param>
/// <param name="ToolTip">Date, insertions and characters saved of the day.</param>
public sealed record DayBarViewModel(double Height, string ToolTip);

/// <summary>One row of the phrase table.</summary>
/// <param name="Phrase">The phrase the row opens.</param>
/// <param name="Name">Phrase name.</param>
/// <param name="Trigger">First trigger, as in the tree.</param>
/// <param name="Uses">Insertions in the window, formatted.</param>
/// <param name="CharactersSaved">Characters saved in the window, formatted.</param>
/// <param name="AccessibleName">What screen readers say for the row.</param>
public sealed record UsageRowViewModel(PhraseViewModel Phrase, string Name, string Trigger, string Uses, string CharactersSaved, string AccessibleName);

/// <summary>
/// Usage of the last 30 days: insertions, characters saved, estimated typing time saved, insertions per day and
/// every phrase used. Built from a <see cref="UsageReport"/> each time the usage or the selection changes.
/// </summary>
public sealed partial class UsageDashboardViewModel : ObservableObject
{
    /// <summary>Height of the tallest bar of the daily chart.</summary>
    public const double ChartHeight = 120;

    private const double MinimumBarHeight = 3;
    private static readonly CultureInfo s_culture = CultureInfo.InvariantCulture;

    /// <summary>True when at least one phrase was used in the window.</summary>
    [ObservableProperty]
    private bool _hasUsage;

    /// <summary>The dates the dashboard covers, for example "13 Sep to 12 Oct 2026".</summary>
    [ObservableProperty]
    private string _rangeText = string.Empty;

    /// <summary>Number of insertions, formatted.</summary>
    [ObservableProperty]
    private string _usesText = "0";

    /// <summary>Label under the number of insertions ("insertion" or "insertions").</summary>
    [ObservableProperty]
    private string _usesLabel = "insertions";

    /// <summary>Number of characters saved, formatted.</summary>
    [ObservableProperty]
    private string _charactersText = "0";

    /// <summary>Estimated typing time saved, for example "1 h 20 min".</summary>
    [ObservableProperty]
    private string _timeText = "0 min";

    /// <summary>The chart in one sentence, for screen readers.</summary>
    [ObservableProperty]
    private string _chartSummary = string.Empty;

    /// <summary>Label under the first bar (the oldest day).</summary>
    [ObservableProperty]
    private string _firstDayText = string.Empty;

    /// <summary>Insertions per day, oldest first.</summary>
    [ObservableProperty]
    private IReadOnlyList<DayBarViewModel> _days = [];

    /// <summary>Phrases used in the window, most used first.</summary>
    [ObservableProperty]
    private IReadOnlyList<UsageRowViewModel> _rows = [];

    /// <summary>Note under the totals that says how time saved is estimated.</summary>
    public static string TimeNote { get; } =
        string.Create(s_culture, $"Time saved is an estimate at {TypingSavings.CharactersPerMinute} characters a minute.");

    /// <summary>Rebuilds the dashboard.</summary>
    /// <param name="report">Usage of the window.</param>
    /// <param name="find">Finds the phrase for an id; phrases it cannot find are left out of the table.</param>
    public void Update(UsageReport report, Func<Guid, PhraseViewModel?> find)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(find);

        var uses = report.TotalUses;
        var characters = report.TotalCharactersSaved;
        HasUsage = uses > 0;
        RangeText = report.Days.Count == 0
            ? string.Empty
            : string.Create(s_culture, $"{report.Days[0].Day:d MMM} to {report.Days[^1].Day:d MMM yyyy}");
        UsesText = uses.ToString("N0", s_culture);
        UsesLabel = uses == 1 ? "insertion" : "insertions";
        CharactersText = characters.ToString("N0", s_culture);
        TimeText = FormatTime(TypingSavings.TypingTime(characters));
        FirstDayText = report.Days.Count == 0 ? string.Empty : report.Days[0].Day.ToString("d MMM", s_culture);

        var max = report.Days.Count == 0 ? 0 : report.Days.Max(d => d.Uses);
        Days = [.. report.Days.Select(d => new DayBarViewModel(
            d.Uses == 0 ? 0 : Math.Max(MinimumBarHeight, ChartHeight * d.Uses / max),
            string.Create(s_culture, $"{d.Day:d MMM}: {Count(d.Uses, "insertion", "insertions")}, {Count(d.CharactersSaved, "character", "characters")} saved")))];
        var busiest = report.Days.Where(d => d.Uses == max).LastOrDefault();
        ChartSummary = busiest is null || max == 0
            ? "Insertions per day: none in the last 30 days."
            : string.Create(s_culture, $"Insertions per day for the last 30 days. The busiest day is {busiest.Day:d MMM} with {Count(busiest.Uses, "insertion", "insertions")}.");

        var rows = new List<UsageRowViewModel>();
        foreach (var totals in report.Phrases)
        {
            if (find(totals.PhraseId) is not { } phrase)
            {
                continue;
            }

            rows.Add(new UsageRowViewModel(
                phrase,
                phrase.DisplayName,
                phrase.TriggerSummary,
                totals.Uses.ToString("N0", s_culture),
                totals.CharactersSaved.ToString("N0", s_culture),
                string.Create(s_culture, $"{phrase.DisplayName}: {Count(totals.Uses, "insertion", "insertions")}, {Count(totals.CharactersSaved, "character", "characters")} saved. Open the phrase.")));
        }

        Rows = rows;
    }

    /// <summary>Formats a typing time, for example "Under 1 min", "45 min" or "1 h 20 min".</summary>
    /// <param name="time">The time.</param>
    internal static string FormatTime(TimeSpan time)
    {
        if (time <= TimeSpan.Zero)
        {
            return "0 min";
        }

        if (time < TimeSpan.FromMinutes(1))
        {
            return "Under 1 min";
        }

        var minutes = (long)Math.Round(time.TotalMinutes, MidpointRounding.AwayFromZero);
        var hours = minutes / 60;
        var rest = minutes % 60;
        return hours == 0
            ? string.Create(s_culture, $"{rest} min")
            : rest == 0 ? string.Create(s_culture, $"{hours} h") : string.Create(s_culture, $"{hours} h {rest} min");
    }

    private static string Count(long value, string one, string many) =>
        value == 1 ? "1 " + one : value.ToString("N0", s_culture) + " " + many;
}
