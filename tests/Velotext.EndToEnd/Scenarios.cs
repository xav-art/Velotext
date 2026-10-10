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

    /// <summary>Reads the document's HTML directly from the app, when copying it to the clipboard is not reliable.</summary>
    public Func<string>? ReadHtml { get; init; }

    /// <summary>
    /// Puts the cursor where the test types, after the window is in front. Returns <c>null</c> on success,
    /// or why the cursor is not there, and then nothing is typed.
    /// </summary>
    public Func<nint, string?>? Focus { get; init; }
}

/// <summary>Runs the standard checks against one application.</summary>
internal static class Scenarios
{
    private static readonly uint s_htmlFormat = Native.RegisterClipboardFormatW("HTML Format");

    /// <summary>Folder where failing checks save what they read (the HTML of a formatting check), for diagnosis.</summary>
    public static string? ArtifactFolder { get; set; }

    /// <summary>
    /// Opens the app before the input guard starts, because guided apps need the user's click. Returns 0
    /// when the app could not open or the user skipped it (the result says which).
    /// </summary>
    public static nint Open(TargetApp app, List<CheckResult> results)
    {
        try
        {
            var window = app.Open();
            if (window == 0)
            {
                results.Add(new CheckResult(app.Name, "Skipped by the user", true, string.Empty));
            }

            return window;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            results.Add(new CheckResult(app.Name, "Open", false, ex.Message));
            return 0;
        }
    }

    public static List<CheckResult> Run(TargetApp app, nint window, Typist typist, bool closeAfter, CancellationToken abort)
    {
        var results = new List<CheckResult>();
        try
        {
            // A failed check also records where keyboard focus was, to tell app behaviour from Velotext's.
            void Check(string name, bool passed, string detail) =>
                results.Add(new CheckResult(app.Name, name, passed, passed ? string.Empty : $"{detail} [{Native.DescribeFocus()}]"));

            void Prepare()
            {
                abort.ThrowIfCancellationRequested();
                if (!Native.ForceForeground(window))
                {
                    throw new InvalidOperationException("The test window could not be brought to the front.");
                }

                if (app.Focus?.Invoke(window) is { } problem)
                {
                    throw new InvalidOperationException(problem);
                }

                typist.Chord(Typist.VkControl, 0x41);
                typist.Press(0x2E);
                Thread.Sleep(300);
                Native.WaitResponsive(window);
            }

            (string Text, string Html) Read()
            {
                // The app may still be busy with the paste; read only once it handles messages again.
                Native.WaitResponsive(window);
                typist.Chord(Typist.VkControl, 0x41);
                typist.Chord(Typist.VkControl, 0x43);
                Thread.Sleep(500);
                var text = (Native.ReadClipboard(Native.CfUnicodeText, utf16: true) ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
                var html = app.ReadHtml?.Invoke() ?? Native.ReadClipboard(s_htmlFormat, utf16: false) ?? string.Empty;
                typist.Press(0x23); // End: drop the selection so the next typing does not replace it
                Native.WaitResponsive(window);
                return (text, html);
            }

            // After a failed text check, read once more after a pause: a passing second read means the app
            // was still busy with the keys; the same text again means keys or the paste never arrived.
            string Again(bool passed)
            {
                if (passed)
                {
                    return string.Empty;
                }

                Thread.Sleep(2000);
                return $"; 2 s later '{Read().Text}'";
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
            if (clipboard != sentinel)
            {
                // Velotext gives the clipboard back a moment after the paste; wait for the next clipboard change.
                var sequence = Native.GetClipboardSequenceNumber();
                var deadline = DateTime.UtcNow.AddSeconds(4);
                while (DateTime.UtcNow < deadline && Native.GetClipboardSequenceNumber() == sequence)
                {
                    Thread.Sleep(50);
                }

                clipboard = Native.ReadClipboard(Native.CfUnicodeText, utf16: true);
            }

            Check("Clipboard restored after paste", clipboard == sentinel, $"clipboard was '{clipboard}'");
            var (text, html) = Read();
            var inserted = text.Contains("Hello World", StringComparison.Ordinal) && text.Contains("second point", StringComparison.Ordinal);
            Check("Formatted phrase inserted", inserted, $"text was '{text}'{Again(inserted)}");
            Check("Trigger text removed", !text.Contains(VelotextInstance.FormattedTrigger, StringComparison.Ordinal) && text.StartsWith("Start ", StringComparison.Ordinal), $"text was '{text}'");
            if (app.ExpectsFormatting)
            {
                // Each check looks for the phrase's own markup, so a mail signature with bold text or links
                // cannot make it pass. Word wraps its HTML source lines, so a line break can sit between words.
                var hasList = System.Text.RegularExpressions.Regex.IsMatch(html, @"first\s+point")
                    && (html.Contains("<li", StringComparison.OrdinalIgnoreCase) || html.Contains("mso-list", StringComparison.OrdinalIgnoreCase) || html.Contains("MsoListParagraph", StringComparison.OrdinalIgnoreCase));
                var hasBold = System.Text.RegularExpressions.Regex.IsMatch(
                    html,
                    @"(<b\b[^>]*>|<strong\b[^>]*>|font-weight:\s*(bold|700)[^>]*>)(\s|<[^>]+>)*World",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var hasLink = html.Contains("href=\"https://example.com", StringComparison.OrdinalIgnoreCase)
                    || html.Contains("href=https://example.com", StringComparison.OrdinalIgnoreCase);
                if (ArtifactFolder is not null && !(hasList && hasBold && hasLink))
                {
                    File.WriteAllText(Path.Combine(ArtifactFolder, $"{app.Name.Replace(' ', '-').Replace("(", string.Empty, StringComparison.Ordinal).Replace(")", string.Empty, StringComparison.Ordinal)}-{DateTime.Now:HHmmss}.html"), html);
                }

                Check("Bullets kept", hasList, "no list markup in the copied HTML");
                Check("Bold kept", hasBold, "no bold markup in the copied HTML");
                Check("Link kept", hasLink, "no link to https://example.com in the copied HTML");
            }

            // 2. Delimiter trigger keeps the space, and text typed right after lands behind the phrase.
            Prepare();
            typist.Type("A " + VelotextInstance.DelimiterTrigger + " tail", window);
            Thread.Sleep(app.SettleMilliseconds);
            (text, _) = Read();
            var kept = text == "A " + VelotextInstance.PlainReply + " tail";
            Check("Delimiter trigger keeps the delimiter", kept, $"text was '{text}'{Again(kept)}");

            // 3. Typing without pauses: keys typed during the expansion are replayed in order.
            Prepare();
            typist.DelayMilliseconds = 0;
            typist.Type(VelotextInstance.FormattedTrigger + " abc", window);
            typist.DelayMilliseconds = 30;
            Thread.Sleep(app.SettleMilliseconds);
            (text, _) = Read();
            var inOrder = text.EndsWith(" abc", StringComparison.Ordinal) && text.Contains("Hello World", StringComparison.Ordinal) && !text.Contains(VelotextInstance.FormattedTrigger, StringComparison.Ordinal);
            Check("Fast typing replayed in order", inOrder, $"text was '{text}'{Again(inOrder)}");

            // 4. Backspace right after an expansion brings the trigger back.
            Prepare();
            typist.Type("Undo " + VelotextInstance.FormattedTrigger, window);
            Thread.Sleep(app.SettleMilliseconds);
            Native.WaitResponsive(window);
            typist.Press(Typist.VkBack);
            Thread.Sleep(app.SettleMilliseconds);
            (text, _) = Read();
            var undone = text == "Undo " + VelotextInstance.FormattedTrigger;
            Check("Backspace undoes the expansion", undone, $"text was '{text}'{Again(undone)}");

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
            var byHotkey = text == "Key: " + VelotextInstance.HotkeyText;
            Check("Hotkey inserts the phrase", byHotkey, $"text was '{text}'{Again(byHotkey)}");

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
                if (closeAfter)
                {
                    app.Close?.Invoke();
                }
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

            // The text area must have the cursor; after a menu was opened it moves to the window's input site.
            Focus = _ =>
            {
                var focus = Native.FocusClass();
                return focus == "RichEditD2DPT" ? null : $"The cursor is in '{focus}', not in the Notepad text; nothing was typed.";
            },
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

            // Copying from the mail editor does not always put HTML on the clipboard; the item's body is exact.
            ReadHtml = () => (string)(mail?.HTMLBody ?? string.Empty),

            // A new mail opens with the cursor in To:. Click into the body (window class "_WwG"), and check
            // that the cursor really is there before anything is typed.
            Focus = window =>
            {
                var body = Native.FindChild(window, "_WwG");
                if (body == 0)
                {
                    return "The email body window was not found; nothing was typed.";
                }

                Native.ClickInside(body, 40, 12);
                Thread.Sleep(400);
                var focus = Native.FocusClass();
                return focus == "_WwG" ? null : $"The cursor is in '{focus}', not in the email body; nothing was typed.";
            },
            Open = () =>
            {
                var type = Type.GetTypeFromProgID("Outlook.Application") ?? throw new InvalidOperationException("Classic Outlook is not installed.");
                created = Process.GetProcessesByName("OUTLOOK").Length == 0;
                app = Activator.CreateInstance(type)!;
                mail = app.CreateItem(0);
                mail.Subject = Subject;
                mail.Display(false);
                return WaitForWindow(Subject, TimeSpan.FromSeconds(60));
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
