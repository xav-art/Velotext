// Velotext: end-to-end runner pieces: input guard, keyboard simulation, Velotext test instance, checks.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Velotext.Core.Model;
using Velotext.Core.RichText;
using Velotext.Core.Settings;
using Velotext.Core.Storage;

namespace Velotext.EndToEnd;

/// <summary>Stops the run as soon as the user touches the keyboard or clicks, so typing never lands in the wrong place.</summary>
internal sealed class InputGuard : IDisposable
{
    private readonly CancellationTokenSource _abort = new();
    private readonly Native.HookProc _keyboard;
    private readonly Native.HookProc _mouse;
    private readonly Thread _thread;
    private uint _threadId;

    public InputGuard()
    {
        _keyboard = OnKeyboard;
        _mouse = OnMouse;
        using var started = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _threadId = Native.GetCurrentThreadId();
            var module = Native.GetModuleHandleW(null);
            var keyboardHook = Native.SetWindowsHookExW(Native.WhKeyboardLl, Marshal.GetFunctionPointerForDelegate(_keyboard), module, 0);
            var mouseHook = Native.SetWindowsHookExW(Native.WhMouseLl, Marshal.GetFunctionPointerForDelegate(_mouse), module, 0);
            started.Set();
            while (Native.GetMessageW(out _, 0, 0, 0) > 0)
            {
            }

            Native.UnhookWindowsHookEx(keyboardHook);
            Native.UnhookWindowsHookEx(mouseHook);
        })
        { IsBackground = true, Name = "Input guard" };
        _thread.Start();
        started.Wait();
    }

    public CancellationToken Token => _abort.Token;

    public string? Reason { get; private set; }

    public void Dispose()
    {
        Native.PostThreadMessageW(_threadId, 0x0012, 0, 0);
        _thread.Join(1000);
        _abort.Dispose();
    }

    private unsafe nint OnKeyboard(int code, nint wParam, nint lParam)
    {
        if (code >= 0 && (((KeyboardHookData*)lParam)->Flags & Native.LlkhfInjected) == 0)
        {
            Abort("a key was pressed");
        }

        return Native.CallNextHookEx(0, code, wParam, lParam);
    }

    private unsafe nint OnMouse(int code, nint wParam, nint lParam)
    {
        var message = (uint)wParam;
        if (code >= 0 && (((MouseHookData*)lParam)->Flags & Native.LlmhfInjected) == 0 && message is 0x201 or 0x204 or 0x207 or 0x20A)
        {
            Abort("the mouse was clicked or scrolled");
        }

        return Native.CallNextHookEx(0, code, wParam, lParam);
    }

    private void Abort(string reason)
    {
        if (!_abort.IsCancellationRequested)
        {
            Reason = reason;
            _abort.Cancel();
        }
    }
}

/// <summary>Types like a person: real key codes for the active layout, with Shift where needed.</summary>
internal sealed class Typist(CancellationToken abort)
{
    public const ushort VkShift = 0x10;
    public const ushort VkControl = 0x11;
    public const ushort VkMenu = 0x12;
    public const ushort VkBack = 0x08;

    public int DelayMilliseconds { get; set; } = 25;

    public void Type(string text, nint window)
    {
        var layout = Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(window, out _));
        foreach (var c in text)
        {
            abort.ThrowIfCancellationRequested();
            var scan = Native.VkKeyScanExW(c, layout);
            if (scan == -1)
            {
                throw new InvalidOperationException($"The keyboard layout cannot type '{c}'.");
            }

            var vk = (ushort)(scan & 0xFF);
            var shift = (scan & 0x100) != 0;
            var keys = new List<(ushort Vk, bool Up)>();
            if (shift)
            {
                keys.Add((VkShift, false));
            }

            keys.Add((vk, false));
            keys.Add((vk, true));
            if (shift)
            {
                keys.Add((VkShift, true));
            }

            Send(keys);
            if (DelayMilliseconds > 0)
            {
                Thread.Sleep(DelayMilliseconds);
            }
        }
    }

    public void Press(ushort vk) => Send([(vk, false), (vk, true)]);

    public void Chord(params ushort[] keys) => Send([.. keys.Select(k => (k, false)), .. keys.Reverse().Select(k => (k, true))]);

    private unsafe void Send(IReadOnlyList<(ushort Vk, bool Up)> keys)
    {
        abort.ThrowIfCancellationRequested();
        var inputs = keys.Select(k => new InputRecord
        {
            Type = Native.InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    Vk = k.Vk,
                    Scan = (ushort)Native.MapVirtualKeyW(k.Vk, 0),
                    Flags = (k.Up ? Native.KeyUp : 0) | (k.Vk is >= 0x21 and <= 0x2E ? Native.ExtendedKey : 0),
                },
            },
        }).ToArray();
        fixed (InputRecord* pointer = inputs)
        {
            Native.SendInput((uint)inputs.Length, pointer, Marshal.SizeOf<InputRecord>());
        }
    }
}

/// <summary>A Velotext instance with its own data folder and a known test library.</summary>
internal sealed class VelotextInstance : IDisposable
{
    public const string FormattedTrigger = "QZXA";
    public const string DelimiterTrigger = "qzxb";
    public const string ClipboardTrigger = "QZXC";
    public const string HotkeyText = "Hotkey phrase from Velotext";
    public const string PlainReply = "Plain reply from Velotext";
    public const string ClipboardPhrase = "Clipboard phrase from Velotext";
    public static readonly HotkeyGesture Hotkey = new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x7A);

    private readonly Process _process;

    public VelotextInstance(string executable)
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "velotext-e2e-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(DataDirectory);
        WriteLibrary();
        File.WriteAllBytes(Path.Combine(DataDirectory, "settings.json"), JsonSerializer.SerializeToUtf8Bytes(new AppSettings { StartWithWindows = false, TrayHintShown = true }, VelotextJsonContext.Default.AppSettings));

        _process = Process.Start(new ProcessStartInfo(executable, ["--autostart", "--verbose", "--data-dir", DataDirectory]) { UseShellExecute = false })
            ?? throw new InvalidOperationException("Velotext did not start.");
        var log = Path.Combine(DataDirectory, "logs");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (Directory.Exists(log) && Directory.EnumerateFiles(log).Any(f => ReadShared(f).Contains("Expansion runtime started", StringComparison.Ordinal)))
            {
                Thread.Sleep(500);
                return;
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException("Velotext did not report that expansion started.");
    }

    public string DataDirectory { get; }

    public static RichDocument FormattedContent => new()
    {
        Blocks =
        [
            new Paragraph { Inlines = [new TextRun { Text = "Hello " }, new TextRun { Text = "World", Style = TextStyle.Bold }] },
            new ListBlock { Kind = ListKind.Bullet, Items = [ListItem.FromText("first point"), ListItem.FromText("second point")] },
            new Paragraph { Inlines = [new Link { Url = "https://example.com/", Runs = [new TextRun { Text = "example site" }] }] },
        ],
    };

    public string Log => string.Join(Environment.NewLine, Directory.EnumerateFiles(Path.Combine(DataDirectory, "logs")).Select(ReadShared));

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(5000);
            }
        }
        finally
        {
            _process.Dispose();
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void WriteLibrary()
    {
        var now = DateTimeOffset.UtcNow;
        Phrase Make(string name, RichDocument content, OutputTarget output, params Trigger[] triggers) =>
            new() { Id = Guid.NewGuid(), Description = name, Content = content, Output = output, Triggers = triggers, CreatedUtc = now, ModifiedUtc = now };

        var library = new PhraseLibrary
        {
            Items =
            [
                new Folder
                {
                    Id = Guid.NewGuid(),
                    Name = "End-to-end",
                    Children =
                    [
                        Make("Formatted", FormattedContent, OutputTarget.ActiveApplication, new AutotextTrigger { Text = FormattedTrigger }),
                        Make("Delimiter", RichDocument.FromPlainText(PlainReply), OutputTarget.ActiveApplication, new AutotextTrigger { Text = DelimiterTrigger, CaseSensitive = false, Immediate = false }),
                        Make("Clipboard", RichDocument.FromPlainText(ClipboardPhrase), OutputTarget.Clipboard, new AutotextTrigger { Text = ClipboardTrigger }),
                        Make("Hotkey", RichDocument.FromPlainText(HotkeyText), OutputTarget.ActiveApplication, new HotkeyTrigger { Gesture = Hotkey }),
                    ],
                },
            ],
        };
        File.WriteAllBytes(Path.Combine(DataDirectory, "library.json"), JsonSerializer.SerializeToUtf8Bytes(library, VelotextJsonContext.Default.PhraseLibrary));
    }
}

/// <summary>Outcome of one check.</summary>
internal sealed record CheckResult(string App, string Check, bool Passed, string Detail);
