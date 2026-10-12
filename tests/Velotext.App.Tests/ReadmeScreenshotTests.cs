// Velotext: renders the README screenshots from a made-up demo library (no personal data), dark and light.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Windows;
using Velotext.App.Services;
using Velotext.App.ViewModels;
using Velotext.App.Views;
using Velotext.Core.Model;
using Velotext.Core.RichText;
using Velotext.Core.Settings;
using Velotext.Core.Storage;
using Velotext.Core.Usage;
using Velotext.Platform.Windows.Lifecycle;

namespace Velotext.App.Tests;

/// <summary>
/// Opens the real main and settings windows with a demo library and 30 days of sample use, and checks that they
/// bind without errors. Set VELOTEXT_README_SCREENSHOTS to a folder (the repo uses .github/screenshots) to save the
/// README images: editor, dashboard and settings, each in dark and light, at 1.5 times the size for sharp text.
/// </summary>
public sealed class ReadmeScreenshotTests
{
    private const double Scale = 1.5;

    private static string? OutputFolder => Environment.GetEnvironmentVariable("VELOTEXT_README_SCREENSHOTS");

    [Theory]
    [InlineData(AppTheme.Dark, "dark")]
    [InlineData(AppTheme.Light, "light")]
    public void Readme_screenshots_render_from_the_demo_library(AppTheme theme, string suffix)
    {
        using var env = new TestEnvironment(DemoData.Write);
        var errors = WpfThread.Run(() => UiRenderTests.CaptureBindingErrors(() =>
        {
            using var themeService = new ThemeService(Application.Current);
            themeService.Apply(theme);
            using var vm = env.CreateMainViewModel();
            var window = new MainWindow(vm) { Left = -20000, Top = -20000, ShowActivated = false, Width = 1200, Height = 820 };
            window.Show();
            try
            {
                vm.Recent.IsExpanded = false;
                vm.SelectedNode = vm.Phrases[DemoData.ThankYouId];
                Assert.Equal("Used 46 times in the last 30 days", vm.EditorUsageText);
                Save(window, $"editor-{suffix}");

                vm.SelectedNode = vm.MostUsed;
                Assert.True(vm.Dashboard.HasUsage);
                Assert.Equal(8, vm.Dashboard.Rows.Count);
                Save(window, $"dashboard-{suffix}");
            }
            finally
            {
                window.CloseRequested += (_, e) => e.Cancel = false;
                window.Close();
            }

            var root = @"Software\Velotext.Tests\" + Guid.NewGuid().ToString("N");
            var settings = new SettingsViewModel(
                env.Settings,
                new AutostartService("VelotextTest", root + @"\Run", root + @"\Approved"),
                env.Library,
                env.Dialogs,
                new SettingsActions(@"C:\Apps\Velotext.exe", env.Directory, env.Paths.LogDirectory, _ => { }, _ => { }, _ => { }, _ => null));
            settings.StartWithWindows = true;
            var settingsWindow = new SettingsWindow(settings) { Left = -20000, Top = -20000, ShowActivated = false };
            settingsWindow.Show();
            try
            {
                Save(settingsWindow, $"settings-{suffix}");
            }
            finally
            {
                settingsWindow.Close();
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(@"Software\Velotext.Tests", throwOnMissingSubKey: false);
            }
        }));

        Assert.Empty(errors);
    }

    private static void Save(Window window, string name)
    {
        if (OutputFolder is { Length: > 0 } folder)
        {
            WindowSnapshot.Save(window, Path.Combine(folder, name + ".png"), Scale);
        }
        else
        {
            window.UpdateLayout();
            WindowSnapshot.Pump();
        }
    }

    /// <summary>A made-up customer service library and 30 days of sample use, written in our own words.</summary>
    private static class DemoData
    {
        public static readonly Guid ThankYouId = new("6f1a2b3c-0d4e-4f50-8a61-72b8c9d0e1f2");

        private static readonly DateTimeOffset s_created = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

        public static void Write(AppPaths paths)
        {
            var phrases = Phrases();
            var library = new PhraseLibrary
            {
                Items =
                [
                    new Folder { Id = Guid.NewGuid(), Name = "Customer service", Children = [.. phrases.Take(5)] },
                    new Folder { Id = Guid.NewGuid(), Name = "Signatures", Children = [.. phrases.Skip(5).Take(2)] },
                    new Folder { Id = Guid.NewGuid(), Name = "Internal", Children = [.. phrases.Skip(7)] },
                ],
            };
            new JsonFileStore<PhraseLibrary>(paths.LibraryFile, VelotextJsonContext.Default.PhraseLibrary, null).Save(library);
            new JsonFileStore<UsageData>(paths.UsageFile, VelotextJsonContext.Default.UsageData, null).Save(Usage(phrases).Snapshot());
        }

        private static UsageTracker Usage(List<Phrase> phrases)
        {
            // Uses per phrase over 30 days (most used first) and a fixed spread, so the images never change.
            int[] totals = [46, 31, 24, 17, 12, 9, 6, 3];
            var tracker = new UsageTracker();
            var now = DateTimeOffset.UtcNow;
            var today = now.UtcDateTime.Date;
            var random = new Random(7);
            for (var p = 0; p < phrases.Count; p++)
            {
                var phrase = phrases[p];
                var typed = phrase.Triggers.OfType<AutotextTrigger>().First().Text.Length;
                var saved = TypingSavings.CharactersSaved(phrase.Content, typed);
                // The most used phrase was also the last one used, so it tops "Recently used" as well.
                var count = p == 0 ? totals[p] - 1 : totals[p];
                for (var use = 0; use < count; use++)
                {
                    // Weekdays get most of the use; days nearer today a bit more.
                    DateTime day;
                    do
                    {
                        day = today.AddDays(-Math.Min(random.Next(30), random.Next(30)));
                    }
                    while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && random.Next(4) > 0);

                    var at = new DateTimeOffset(DateTime.SpecifyKind(day, DateTimeKind.Utc).AddHours(8 + random.Next(9)), TimeSpan.Zero);
                    tracker.RecordUse(phrase.Id, at < now ? at : now.AddMinutes(-1 - p), saved);
                }

                if (p == 0)
                {
                    tracker.RecordUse(phrase.Id, now, saved);
                }
            }

            return tracker;
        }

        private static List<Phrase> Phrases() =>
        [
            Phrase(
                ThankYouId,
                "Thank you for waiting",
                "/thx",
                Text("Hi,"),
                Text("Thank you for waiting. I checked your order, and this is what happens next:"),
                Bullets(Runs(("Your parcel leaves our warehouse ", TextStyle.None), ("today", TextStyle.Bold), (".", TextStyle.None)), Runs(("You get a tracking link by email.", TextStyle.None)), Runs(("Delivery takes 2 to 4 working days.", TextStyle.None))),
                new Paragraph { Inlines = [new TextRun { Text = "You can follow your parcel on " }, new Link { Url = "https://example.com/track", Runs = [new TextRun { Text = "our tracking page" }] }, new TextRun { Text = "." }] }),
            Phrase(Guid.NewGuid(), "Order status", "/status", Text("Your order is on its way. You can expect it within 3 working days.")),
            Phrase(Guid.NewGuid(), "Refund confirmed", "/refund", Text("We have refunded your payment. The money is back on your card within 5 working days.")),
            Phrase(Guid.NewGuid(), "Delivery delay", "/delay", Text("We are sorry: your parcel is delayed by 1 or 2 days because of high demand.")),
            Phrase(Guid.NewGuid(), "Return label", "/return", Text("I attached a free return label. Print it, stick it on the box and drop the parcel at any post office.")),
            Phrase(Guid.NewGuid(), "Kind regards", "/kr", Text("Kind regards,"), new Paragraph { Inlines = [new TextRun { Text = "Customer Service", Style = TextStyle.Bold }] }),
            Phrase(Guid.NewGuid(), "Out of office", "/ooo", Text("I am out of the office until Monday. For urgent questions, write to the team inbox.")),
            Phrase(
                Guid.NewGuid(),
                "Meeting notes",
                "/notes",
                Text("Notes from today's meeting:"),
                new ListBlock { Kind = ListKind.Numbered, Items = [ListItem.FromText("Decisions"), ListItem.FromText("Actions and owners"), ListItem.FromText("Next meeting")] }),
        ];

        private static Phrase Phrase(Guid id, string name, string trigger, params Block[] blocks) => new()
        {
            Id = id,
            Description = name,
            Content = new RichDocument { Blocks = blocks },
            Triggers = trigger == "/kr"
                ? [new AutotextTrigger { Text = trigger }, new HotkeyTrigger { Gesture = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x4B) }]
                : [new AutotextTrigger { Text = trigger }],
            CreatedUtc = s_created,
            ModifiedUtc = s_created,
        };

        private static Paragraph Text(string text) => Paragraph.FromText(text);

        private static TextRun[] Runs(params (string Text, TextStyle Style)[] runs) => [.. runs.Select(r => new TextRun { Text = r.Text, Style = r.Style })];

        private static ListBlock Bullets(params TextRun[][] items) => new()
        {
            Kind = ListKind.Bullet,
            Items = [.. items.Select(runs => new ListItem { Blocks = [new Paragraph { Inlines = runs }] })],
        };
    }
}
