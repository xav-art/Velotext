// Velotext: memory measurement mode of the end-to-end runner (tray, open, hidden, released) without typing anything.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Velotext.Core.Model;
using Velotext.Core.RichText;
using Velotext.Core.Settings;
using Velotext.Core.Storage;

namespace Velotext.EndToEnd;

/// <summary>
/// Measures the memory of a Velotext test copy at fixed stages of a normal day: started by Windows into the
/// tray, window opened, window closed to the tray, and after the hidden window was released. It also times
/// how long the window takes to appear. It never types: the window is opened by starting a second copy
/// (which asks the first one to show itself) and closed with WM_CLOSE, so the user can keep working.
/// </summary>
internal static partial class MemoryProbe
{
    private const uint WmClose = 0x0010;
    private static readonly JsonSerializerOptions s_reportOptions = new() { WriteIndented = true };

    /// <summary>Runs the measurement and prints a table.</summary>
    /// <param name="executable">Velotext.exe to measure.</param>
    /// <param name="releaseWait">How long to wait with the window hidden before the last sample.</param>
    /// <param name="phrases">Number of synthetic phrases in the test library.</param>
    /// <returns>The process exit code.</returns>
    public static int Run(string executable, TimeSpan releaseWait, int phrases)
    {
        var folder = Path.Combine(Path.GetTempPath(), "velotext-memory-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        WriteLibrary(folder, phrases);
        File.WriteAllBytes(Path.Combine(folder, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(new AppSettings { StartWithWindows = false, TrayHintShown = true }, VelotextJsonContext.Default.AppSettings));

        Console.WriteLine($"Measuring {executable}");
        Console.WriteLine($"Data folder {folder}, {phrases} phrases, release wait {releaseWait.TotalSeconds:0} s");
        var samples = new List<Sample>();
        using var process = Process.Start(new ProcessStartInfo(executable, ["--autostart", "--verbose", "--data-dir", folder]) { UseShellExecute = false })
            ?? throw new InvalidOperationException("Velotext did not start.");
        try
        {
            var started = Stopwatch.StartNew();
            WaitForLog(folder, "Expansion runtime started", TimeSpan.FromSeconds(30));
            var startup = started.Elapsed;
            Thread.Sleep(TimeSpan.FromSeconds(8));
            samples.Add(Take(process, "Tray (started by Windows)", startup));

            var window = Open(executable, folder, process, out var openTime);
            Thread.Sleep(TimeSpan.FromSeconds(8));
            samples.Add(Take(process, "Window open (first time)", openTime));

            Native.PostMessageW(window, WmClose, 0, 0);
            Thread.Sleep(TimeSpan.FromSeconds(8));
            samples.Add(Take(process, "Window closed to tray", null));

            Thread.Sleep(releaseWait);
            samples.Add(Take(process, "Hidden for the release wait", null));

            window = Open(executable, folder, process, out openTime);
            Thread.Sleep(TimeSpan.FromSeconds(8));
            samples.Add(Take(process, "Window open (again)", openTime));

            Native.PostMessageW(window, WmClose, 0, 0);
            Thread.Sleep(TimeSpan.FromSeconds(3));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{"Stage",-30} {"Private WS",11} {"Working set",12} {"Commit",9} {"Modules",8} {"GPU",4} {"Time",8}");
        foreach (var s in samples)
        {
            var time = s.Elapsed is { } e ? $"{e.TotalMilliseconds:0} ms" : string.Empty;
            Console.WriteLine($"{s.Stage,-30} {s.PrivateWorkingSetMb,8:0.0} MB {s.WorkingSetMb,9:0.0} MB {s.CommitMb,6:0.0} MB {s.Modules,8} {s.GpuModules.Count,4} {time,8}");
        }

        var report = Path.Combine(folder, "memory-results.json");
        File.WriteAllText(report, JsonSerializer.Serialize(samples, s_reportOptions));
        Console.WriteLine($"GPU driver modules at the end: {string.Join(", ", samples[^1].GpuModules.DefaultIfEmpty("none"))}");
        Console.WriteLine($"Report: {report}");
        return 0;
    }

    /// <summary>Asks the running copy to show its window (as the tray icon or a second start does) and times it.</summary>
    private static nint Open(string executable, string folder, Process process, out TimeSpan elapsed)
    {
        var watch = Stopwatch.StartNew();
        using (var second = Process.Start(new ProcessStartInfo(executable, ["--data-dir", folder]) { UseShellExecute = false }))
        {
            second?.WaitForExit(30_000);
        }

        Console.WriteLine($"  second copy handed over in {watch.ElapsedMilliseconds} ms");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var window = Native.FindWindow("Velotext", (uint)process.Id);
            if (window != 0)
            {
                Native.WaitResponsive(window);
                elapsed = watch.Elapsed;
                return window;
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException("The Velotext window did not appear.");
    }

    private static void WaitForLog(string folder, string text, TimeSpan timeout)
    {
        var log = Path.Combine(folder, "logs");
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(log) && Directory.EnumerateFiles(log).Any(f => ReadShared(f).Contains(text, StringComparison.Ordinal)))
            {
                return;
            }

            Thread.Sleep(50);
        }

        throw new TimeoutException($"Velotext did not log \"{text}\".");
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Sample Take(Process process, string stage, TimeSpan? elapsed)
    {
        process.Refresh();
        var counters = new ProcessMemoryCountersEx2 { Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
        if (!GetProcessMemoryInfo(process.Handle, ref counters, counters.Size))
        {
            throw new InvalidOperationException("GetProcessMemoryInfo failed: " + Marshal.GetLastPInvokeError());
        }

        var modules = process.Modules.Cast<ProcessModule>().Select(m => m.ModuleName).ToList();
        var gpu = modules.Where(IsGpuDriver).Order(StringComparer.OrdinalIgnoreCase).ToList();
        const double Mb = 1024 * 1024;
        Console.WriteLine($"  sampled: {stage}");
        return new Sample(stage, counters.PrivateWorkingSetSize / Mb, counters.WorkingSetSize / Mb, counters.PrivateUsage / Mb, modules.Count, gpu, elapsed);
    }

    /// <summary>True for user-mode graphics driver modules (Intel, NVIDIA, AMD) and the Direct3D 9 runtime.</summary>
    private static bool IsGpuDriver(string name)
    {
        var lower = name.ToLowerInvariant();
        return lower.StartsWith("igd", StringComparison.Ordinal) || lower.StartsWith("igc", StringComparison.Ordinal)
            || lower.StartsWith("media_bin", StringComparison.Ordinal) || lower.StartsWith("nvd3d", StringComparison.Ordinal)
            || lower.StartsWith("nvwgf", StringComparison.Ordinal) || lower.StartsWith("nvldumd", StringComparison.Ordinal)
            || lower.StartsWith("aticfx", StringComparison.Ordinal) || lower.StartsWith("atiumd", StringComparison.Ordinal)
            || lower.StartsWith("amdx", StringComparison.Ordinal) || lower == "d3d9.dll";
    }

    /// <summary>Writes a synthetic library: folders of formatted phrases with typed triggers (no real phrases).</summary>
    private static void WriteLibrary(string folder, int phrases)
    {
        var now = DateTimeOffset.UtcNow;
        var text = new StringBuilder();
        for (var i = 0; i < 12; i++)
        {
            text.Append("This is synthetic sentence number ").Append(i).Append(" for the memory test. ");
        }

        var body = text.ToString();
        var folders = new List<LibraryNode>();
        for (var f = 0; f * 12 < phrases; f++)
        {
            var children = new List<LibraryNode>();
            for (var p = f * 12; p < Math.Min(phrases, (f + 1) * 12); p++)
            {
                var content = new RichDocument
                {
                    Blocks =
                    [
                        new Paragraph { Inlines = [new TextRun { Text = "Hello " }, new TextRun { Text = "customer", Style = TextStyle.Bold }, new TextRun { Text = "," }] },
                        new Paragraph { Inlines = [new TextRun { Text = body }] },
                        new ListBlock { Kind = ListKind.Bullet, Items = [ListItem.FromText("first point"), ListItem.FromText("second point")] },
                        new Paragraph { Inlines = [new Link { Url = "https://example.com/", Runs = [new TextRun { Text = "example site" }] }] },
                    ],
                };
                children.Add(new Phrase
                {
                    Id = Guid.NewGuid(),
                    Description = $"Synthetic phrase {p + 1}",
                    Content = content,
                    Triggers = [new AutotextTrigger { Text = $"qzm{p:000}" }],
                    CreatedUtc = now,
                    ModifiedUtc = now,
                });
            }

            folders.Add(new Folder { Id = Guid.NewGuid(), Name = $"Folder {f + 1}", Children = children });
        }

        var library = new PhraseLibrary { Items = folders };
        File.WriteAllBytes(Path.Combine(folder, "library.json"), JsonSerializer.SerializeToUtf8Bytes(library, VelotextJsonContext.Default.PhraseLibrary));
    }

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCountersEx2 counters, uint size);

    /// <summary>PROCESS_MEMORY_COUNTERS_EX2: includes the private working set that Task Manager shows as "Memory".</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    /// <summary>One measurement.</summary>
    internal sealed record Sample(string Stage, double PrivateWorkingSetMb, double WorkingSetMb, double CommitMb, int Modules, IReadOnlyList<string> GpuModules, TimeSpan? Elapsed);
}
