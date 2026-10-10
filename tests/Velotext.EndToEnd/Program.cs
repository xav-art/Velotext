// Velotext: end-to-end runner entry point: warns the user, starts a test instance and runs every app scenario.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Velotext.EndToEnd;

/// <summary>
/// Runs Velotext against real applications. It types into windows it opens itself, never sends email or
/// chat messages, and stops at once when the user presses a key or clicks.
/// Usage: Velotext.EndToEnd [--apps notepad,word,outlook,edge,chrome,newoutlook,teams] [--velotext path] [--countdown 10].
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions s_reportOptions = new() { WriteIndented = true };
    private static readonly string[] s_configurations = ["Release", "Debug"];

    [STAThread]
    private static int Main(string[] args)
    {
        var apps = Option(args, "--apps")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? ["notepad", "word", "outlook", "edge", "chrome"];
        var velotext = Option(args, "--velotext") ?? FindVelotext();
        var countdown = int.Parse(Option(args, "--countdown") ?? "10", CultureInfo.InvariantCulture);

        if (!Prompt.Countdown(
            "Velotext end-to-end test",
            $"This test types into: {string.Join(", ", apps)}.\nKeep your hands off the keyboard and mouse. Any key press or click stops the test.\nNothing is sent: test emails are discarded and chat text is cleared.",
            countdown))
        {
            Console.WriteLine("Cancelled.");
            return 2;
        }

        var results = new List<CheckResult>();
        string? stopReason = null;
        using var instance = new VelotextInstance(velotext);
        var folder = instance.DataDirectory;
        foreach (var name in apps)
        {
            var app = name.ToUpperInvariant() switch
            {
                "NOTEPAD" => Scenarios.Notepad(folder),
                "WORD" => Scenarios.Word(),
                "OUTLOOK" => Scenarios.ClassicOutlook(),
                "EDGE" => Scenarios.Browser("Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe", folder),
                "CHROME" => Scenarios.Browser("Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", folder),
                "NEWOUTLOOK" => Scenarios.Guided("Outlook (new)", () => Prompt.Ask("New Outlook", "Open new Outlook, start a new email and click in the message body. Then select Continue and keep your hands off the keyboard.")),
                "TEAMS" => Scenarios.Guided("Teams", () => Prompt.Ask("Teams", "Open Teams, open the chat with yourself and click in the message box. Then select Continue and keep your hands off the keyboard. Velotext never presses Enter.")),
                _ => null,
            };
            if (app is null)
            {
                results.Add(new CheckResult(name, "Known app", false, "Unknown app name."));
                continue;
            }

            Console.WriteLine($"Running {app.Name}...");
            using var guard = new InputGuard();
            try
            {
                results.AddRange(Scenarios.Run(app, new Typist(guard.Token), guard.Token));
            }
            catch (OperationCanceledException)
            {
                stopReason = guard.Reason ?? "cancelled";
                break;
            }
        }

        var log = instance.Log;
        var report = Path.Combine(folder, "e2e-results.json");
        File.WriteAllText(report, JsonSerializer.Serialize(results, s_reportOptions));
        File.WriteAllText(Path.Combine(folder, "velotext.log"), log);

        Console.WriteLine();
        foreach (var group in results.GroupBy(r => r.App))
        {
            Console.WriteLine(group.Key);
            foreach (var result in group)
            {
                Console.WriteLine($"  [{(result.Passed ? "PASS" : "FAIL")}] {result.Check}{(result.Passed ? string.Empty : ": " + result.Detail)}");
            }
        }

        if (stopReason is not null)
        {
            Console.WriteLine($"Stopped early because {stopReason}.");
        }

        Console.WriteLine($"Report: {report}");
        return results.All(r => r.Passed) && stopReason is null ? 0 : 1;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string FindVelotext()
    {
        var root = AppContext.BaseDirectory;
        for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
        {
            var candidates = s_configurations
                .Select(c => Path.Combine(directory.FullName, "src", "Velotext.App", "bin", c, "net10.0-windows", "Velotext.exe"))
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();
            if (candidates.Count > 0)
            {
                return candidates[0];
            }
        }

        throw new FileNotFoundException("Build Velotext first, or pass --velotext <path to Velotext.exe>.");
    }
}

/// <summary>Small top-most windows that warn the user and ask for a step.</summary>
internal static class Prompt
{
    public static bool Countdown(string title, string message, int seconds)
    {
        var remaining = seconds;
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 0, 0, 16) };
        var counter = new TextBlock { FontSize = 28, FontWeight = FontWeights.SemiBold };
        var cancel = new Button { Content = "Cancel test", Padding = new Thickness(16, 6, 16, 6), HorizontalAlignment = HorizontalAlignment.Right, IsCancel = true };
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(text);
        panel.Children.Add(counter);
        panel.Children.Add(cancel);
        var window = new Window { Title = title, Content = panel, Width = 520, SizeToContent = SizeToContent.Height, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize };
        var started = false;
        cancel.Click += (_, _) => window.Close();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            remaining--;
            counter.Text = $"Starting in {remaining} s";
            if (remaining <= 0)
            {
                started = true;
                timer.Stop();
                window.Close();
            }
        };
        counter.Text = $"Starting in {remaining} s";
        timer.Start();
        window.ShowDialog();
        timer.Stop();
        return started;
    }

    public static bool Ask(string title, string message)
    {
        var result = false;
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 0, 0, 16) };
        var go = new Button { Content = "Continue", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        var skip = new Button { Content = "Skip", Padding = new Thickness(16, 6, 16, 6), IsCancel = true };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(go);
        buttons.Children.Add(skip);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(text);
        panel.Children.Add(buttons);
        var window = new Window { Title = title, Content = panel, Width = 520, SizeToContent = SizeToContent.Height, Topmost = true, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize };
        go.Click += (_, _) =>
        {
            result = true;
            window.Close();
        };
        skip.Click += (_, _) => window.Close();
        window.ShowDialog();
        return result;
    }
}
