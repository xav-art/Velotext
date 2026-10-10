// Velotext: end-to-end scenarios: the same checks run in every target application.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Velotext.EndToEnd;

/// <summary>A target application: how to open it, read its text and close it.</summary>
internal sealed class TargetApp
{
    public required string Name { get; init; }

    public required Func<nint> Open { get; init; }

    public Action? Close { get; init; }

    public bool ExpectsFormatting { get; init; } = true;

    public int SettleMilliseconds { get; init; } = 1500;
}

/// <summary>Runs the standard checks against one application.</summary>
internal static class Scenarios
{
    private static readonly uint s_htmlFormat = Native.RegisterClipboardFormatW("HTML Format");

    public static List<CheckResult> Run(TargetApp app, Typist typist, CancellationToken abort)
    {
        var results = new List<CheckResult>();
        nint window;
        try
        {
            window = app.Open();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            results.Add(new CheckResult(app.Name, "Open", false, ex.Message));
            return results;
        }

        if (window == 0)
        {
            results.Add(new CheckResult(app.Name, "Skipped by the user", true, string.Empty));
            return results;
        }

        try
        {
            void Check(string name, bool passed, string detail) => results.Add(new CheckResult(app.Name, name, passed, passed ? string.Empty : detail));

            void Prepare()
            {
                abort.ThrowIfCancellationRequested();
                if (!Native.ForceForeground(window))
                {
                    throw new InvalidOperationException("The test window could not be brought to the front.");
                }

                typist.Chord(Typist.VkControl, 0x41);
                typist.Press(0x2E);
                Thread.Sleep(300);
            }

            (string Text, string Html) Read()
            {
                typist.Chord(Typist.VkControl, 0x41);
                typist.Chord(Typist.VkControl, 0x43);
                Thread.Sleep(500);
                var text = (Native.ReadClipboard(Native.CfUnicodeText, utf16: true) ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
                var html = Native.ReadClipboard(s_htmlFormat, utf16: false) ?? string.Empty;
                typist.Press(0x23); // End: drop the selection so the next typing does not replace it
                return (text, html);
            }

            string Sentinel()
            {
                var sentinel = "VELOTEXT-SENTINEL-" + Guid.NewGuid().ToString("N")[..8];
                Native.WriteClipboardText(sentinel);
                return sentinel;
            }

            // 1. Immediate formatted trigger; the user's clipboard comes back afterwards.
            Prepare();
            var sentinel = Sentinel();
            typist.DelayMilliseconds = 30;
            typist.Type("Start " + VelotextInstance.FormattedTrigger, window);
            Thread.Sleep(app.SettleMilliseconds);
            var clipboard = Native.ReadClipboard(Native.CfUnicodeText, utf16: true);
            Check("Clipboard restored after paste", clipboard == sentinel, $"clipboard was '{clipboard}'");
            var (text, html) = Read();
            Check("Formatted phrase inserted", text.Contains("Hello World", StringComparison.Ordinal) && text.Contains("second point", StringComparison.Ordinal), $"text was '{text}'");
            Check("Trigger text removed", !text.Contains(VelotextInstance.FormattedTrigger, StringComparison.Ordinal) && text.StartsWith("Start ", StringComparison.Ordinal), $"text was '{text}'");
            if (app.ExpectsFormatting)
            {
                var hasList = html.Contains("<li", StringComparison.OrdinalIgnoreCase) || html.Contains("mso-list", StringComparison.OrdinalIgnoreCase) || html.Contains("MsoListParagraph", StringComparison.OrdinalIgnoreCase);
                var hasBold = html.Contains("<b>", StringComparison.OrdinalIgnoreCase) || html.Contains("<strong", StringComparison.OrdinalIgnoreCase) || html.Contains("font-weight:bold", StringComparison.OrdinalIgnoreCase) || html.Contains("font-weight:700", StringComparison.OrdinalIgnoreCase) || html.Contains("<b ", StringComparison.OrdinalIgnoreCase);
                Check("Bullets kept", hasList, "no list markup in the copied HTML");
                Check("Bold kept", hasBold, "no bold markup in the copied HTML");
                Check("Link kept", html.Contains("https://example.com", StringComparison.OrdinalIgnoreCase), "no link in the copied HTML");
            }

            // 2. Delimiter trigger keeps the space, and text typed right after lands behind the phrase.
            Prepare();
            typist.Type("A " + VelotextInstance.DelimiterTrigger + " tail", window);
            Thread.Sleep(app.SettleMilliseconds);
            (text, _) = Read();
            Check("Delimiter trigger keeps the delimiter", text == "A " + VelotextInstance.PlainReply + " tail", $"text was '{text}'");

            // 3. Typing without pauses: keys typed during the expansion are replayed in order.
            Prepare();
            typist.DelayMilliseconds = 0;
            typist.Type(VelotextInstance.FormattedTrigger + " abc", window);
            typist.DelayMilliseconds = 30;
            Thread.Sleep(app.SettleMilliseconds);
            (text, _) = Read();
            Check("Fast typing replayed in order", text.EndsWith(" abc", StringComparison.Ordinal) && text.Contains("Hello World", StringComparison.Ordinal) && !text.Contains(VelotextInstance.FormattedTrigger, StringComparison.Ordinal), $"text was '{text}'");

            // 4. Backspace right after an expansion brings the trigger back.
            Prepare();
            typist.Type("Undo " + VelotextInstance.FormattedTrigger, window);
            Thread.Sleep(app.SettleMilliseconds);
            typist.Press(Typist.VkBack);
            Thread.Sleep(app.SettleMilliseconds);
            (text, _) = Read();
            Check("Backspace undoes the expansion", text == "Undo " + VelotextInstance.FormattedTrigger, $"text was '{text}'");

            // 5. Clipboard output target: the trigger disappears and the phrase waits on the clipboard.
            Prepare();
            Sentinel();
            typist.Type("Copy " + VelotextInstance.ClipboardTrigger, window);
            Thread.Sleep(app.SettleMilliseconds);
            clipboard = Native.ReadClipboard(Native.CfUnicodeText, utf16: true);
            Check("Clipboard target copies the phrase", clipboard == VelotextInstance.ClipboardPhrase, $"clipboard was '{clipboard}'");
            (text, _) = Read();
            Check("Clipboard target removes the trigger", text == "Copy", $"text was '{text}'");

            // 6. Hotkey inserts the phrase at the caret.
            Prepare();
            typist.Type("Key: ", window);
            typist.Chord(Typist.VkControl, Typist.VkMenu, Typist.VkShift, (ushort)VelotextInstance.Hotkey.Key);
            Thread.Sleep(app.SettleMilliseconds);
            (text, _) = Read();
            Check("Hotkey inserts the phrase", text == "Key: " + VelotextInstance.HotkeyText, $"text was '{text}'");

            Prepare();
        }
        catch (OperationCanceledException)
        {
            results.Add(new CheckResult(app.Name, "Run", false, "Stopped by user input"));
            throw;
        }
        catch (Exception ex)
        {
            results.Add(new CheckResult(app.Name, "Run", false, ex.Message));
        }
        finally
        {
            try
            {
                app.Close?.Invoke();
            }
            catch (Exception ex)
            {
                results.Add(new CheckResult(app.Name, "Close", false, ex.Message));
            }
        }

        return results;
    }

    public static TargetApp Notepad(string folder)
    {
        var file = Path.Combine(folder, "notepad-e2e.txt");
        File.WriteAllText(file, string.Empty);
        Process? process = null;
        return new TargetApp
        {
            Name = "Notepad",
            ExpectsFormatting = false,
            Open = () =>
            {
                process = Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true });
                return WaitForWindow("notepad-e2e", TimeSpan.FromSeconds(15));
            },
            Close = () =>
            {
                var window = Native.FindWindow("notepad-e2e");
                if (window != 0 && Native.ForceForeground(window))
                {
                    // Close only our tab, without saving.
                    var typist = new Typist(CancellationToken.None);
                    typist.Chord(Typist.VkControl, 0x57);
                    Thread.Sleep(800);
                    var prompt = Native.GetForegroundWindow();
                    if (Native.WindowTitle(prompt).Contains("Notepad", StringComparison.OrdinalIgnoreCase))
                    {
                        typist.Press(0x4E); // "Don't save" accelerator
                    }
                }

                process?.Dispose();
            },
        };
    }

    public static TargetApp Browser(string name, string executable, string folder)
    {
        var profile = Path.Combine(folder, name + "-profile");
        var page = new Uri(Path.Combine(AppContext.BaseDirectory, "TestPage.html")).AbsoluteUri;
        Process? process = null;
        return new TargetApp
        {
            Name = name,
            Open = () =>
            {
                if (!File.Exists(executable))
                {
                    throw new FileNotFoundException($"{name} is not installed at {executable}.");
                }

                process = Process.Start(new ProcessStartInfo(executable, ["--new-window", "--no-first-run", "--no-default-browser-check", "--disable-sync", $"--user-data-dir={profile}", page]) { UseShellExecute = false });
                var window = WaitForWindow("Velotext E2E editor", TimeSpan.FromSeconds(30));
                Thread.Sleep(1500);
                return window;
            },
            Close = () =>
            {
                try
                {
                    process?.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already gone.
                }

                process?.Dispose();
            },
        };
    }

    public static TargetApp Word()
    {
        dynamic? app = null;
        dynamic? document = null;
        var created = false;
        return new TargetApp
        {
            Name = "Word",
            SettleMilliseconds = 2000,
            Open = () =>
            {
                var type = Type.GetTypeFromProgID("Word.Application") ?? throw new InvalidOperationException("Word is not installed.");
                created = Process.GetProcessesByName("WINWORD").Length == 0;
                app = Activator.CreateInstance(type)!;
                app.Visible = true;
                document = app.Documents.Add();
                document.Activate();
                string caption = document.ActiveWindow.Caption;
                return WaitForWindow(caption, TimeSpan.FromSeconds(15));
            },
            Close = () =>
            {
                document?.Close(0);
                if (created)
                {
                    app?.Quit(0);
                }

                if (app is not null)
                {
                    Marshal.FinalReleaseComObject(app);
                }
            },
        };
    }

    public static TargetApp ClassicOutlook()
    {
        dynamic? app = null;
        dynamic? mail = null;
        var created = false;
        const string Subject = "Velotext end-to-end test (never sent)";
        return new TargetApp
        {
            Name = "Outlook (classic)",
            SettleMilliseconds = 2500,
            Open = () =>
            {
                var type = Type.GetTypeFromProgID("Outlook.Application") ?? throw new InvalidOperationException("Classic Outlook is not installed.");
                created = Process.GetProcessesByName("OUTLOOK").Length == 0;
                app = Activator.CreateInstance(type)!;
                mail = app.CreateItem(0);
                mail.Subject = Subject;
                mail.Display(false);
                var window = WaitForWindow(Subject, TimeSpan.FromSeconds(60));
                dynamic editor = mail.GetInspector.WordEditor;
                editor.Range(0, 0).Select();
                return window;
            },
            Close = () =>
            {
                mail?.Close(1); // olDiscard: never saved, never sent
                if (created)
                {
                    app?.Quit();
                }

                if (app is not null)
                {
                    Marshal.FinalReleaseComObject(app);
                }
            },
        };
    }

    public static TargetApp Guided(string name, Func<bool> askUser)
    {
        return new TargetApp
        {
            Name = name,
            SettleMilliseconds = 2500,
            Open = () =>
            {
                if (!askUser())
                {
                    return 0;
                }

                Thread.Sleep(1000);
                return Native.GetForegroundWindow();
            },
        };
    }

    private static nint WaitForWindow(string titlePart, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var window = Native.FindWindow(titlePart);
            if (window != 0)
            {
                return window;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException($"No window with '{titlePart}' in its title appeared.");
    }
}
