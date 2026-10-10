// Velotext: opt-in tests that use the real clipboard, hotkeys and keyboard hook (set VELOTEXT_DESKTOP_TESTS=1).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Velotext.Core.Input;
using Velotext.Core.Model;
using Velotext.Platform.Windows.Clipboard;
using Velotext.Platform.Windows.Hotkeys;
using Velotext.Platform.Windows.Input;
using Velotext.Platform.Windows.Native;
using Velotext.Platform.Windows.Threading;

namespace Velotext.Platform.Windows.Tests;

/// <summary>
/// Tests against the real desktop. They briefly replace the clipboard (text is put back afterwards) and
/// register an unusual hotkey, so they only run when VELOTEXT_DESKTOP_TESTS=1.
/// </summary>
public sealed class DesktopTests
{
    private const string SkipReason = "Set VELOTEXT_DESKTOP_TESTS=1 to run tests that use the real clipboard, hotkeys and keyboard hook.";

    public static bool Enabled => Environment.GetEnvironmentVariable("VELOTEXT_DESKTOP_TESTS") == "1";

    private static unsafe string? ReadText(uint format, nint window, bool utf16)
    {
        for (var attempt = 0; attempt < 50 && !NativeMethods.OpenClipboard(window); attempt++)
        {
            Thread.Sleep(10);
        }

        try
        {
            var handle = NativeMethods.GetClipboardData(format);
            if (handle == 0)
            {
                return null;
            }

            var pointer = NativeMethods.GlobalLock(handle);
            try
            {
                var size = (int)NativeMethods.GlobalSize(handle);
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, size);
                return (utf16 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes)).TrimEnd('\0');
            }
            finally
            {
                NativeMethods.GlobalUnlock(handle);
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    [Fact(SkipUnless = nameof(Enabled), Skip = SkipReason)]
    public async Task Clipboard_paste_is_detected_when_target_reads_and_original_is_restored()
    {
        using var clipboard = new ClipboardService(NullLogger<ClipboardService>.Instance);
        using var reader = new MessageLoopThread("Test reader", ThreadPriority.Normal, null, _ => { });
        var userText = await reader.InvokeAsync(() => ReadText(NativeMethods.CfUnicodeText, reader.WindowHandle, utf16: true));
        try
        {
            Assert.True(await clipboard.CopyAsync(new ClipboardPayload("original", null, null)));

            var html = "Version:0.9\r\n<b>x</b>";
            var session = await clipboard.BeginPasteAsync(new ClipboardPayload("phrase", html, "{\\rtf1 x}"), (uint)Environment.ProcessId);
            Assert.NotNull(session);
            Assert.False(session.Consumed.IsCompleted);
            session.MarkPasteSent();

            // Act as the target application: open the clipboard from a window of this process and read.
            var htmlFormat = NativeMethods.RegisterClipboardFormatW("HTML Format");
            var readHtml = await reader.InvokeAsync(() => ReadText(htmlFormat, reader.WindowHandle, utf16: false));
            Assert.Equal(html, readHtml);
            Assert.Equal(reader.WindowHandle, await session.Consumed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

            await clipboard.RestoreAsync(session);
            Assert.Equal("original", await reader.InvokeAsync(() => ReadText(NativeMethods.CfUnicodeText, reader.WindowHandle, utf16: true)));
        }
        finally
        {
            await clipboard.CopyAsync(new ClipboardPayload(userText ?? string.Empty, null, null));
        }
    }

    [Fact(SkipUnless = nameof(Enabled), Skip = SkipReason)]
    public async Task Clipboard_replaced_by_someone_else_is_not_restored()
    {
        using var clipboard = new ClipboardService(NullLogger<ClipboardService>.Instance);
        using var other = new ClipboardService(NullLogger<ClipboardService>.Instance);
        using var reader = new MessageLoopThread("Test reader", ThreadPriority.Normal, null, _ => { });
        var userText = await reader.InvokeAsync(() => ReadText(NativeMethods.CfUnicodeText, reader.WindowHandle, utf16: true));
        try
        {
            await clipboard.CopyAsync(new ClipboardPayload("original", null, null));
            var session = await clipboard.BeginPasteAsync(new ClipboardPayload("phrase", null, null), 1);
            Assert.NotNull(session);

            await other.CopyAsync(new ClipboardPayload("user copied something new", null, null));
            await session.Consumed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(session.ReplacedByOther);

            await clipboard.RestoreAsync(session);
            Assert.Equal("user copied something new", await reader.InvokeAsync(() => ReadText(NativeMethods.CfUnicodeText, reader.WindowHandle, utf16: true)));
        }
        finally
        {
            await clipboard.CopyAsync(new ClipboardPayload(userText ?? string.Empty, null, null));
        }
    }

    [Fact(SkipUnless = nameof(Enabled), Skip = SkipReason)]
    public async Task Hotkeys_register_and_duplicates_are_reported()
    {
        using var hotkeys = new GlobalHotkeyService(NullLogger<GlobalHotkeyService>.Instance);
        var unusual = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x87);

        var failed = await hotkeys.ReplaceAllAsync([new HotkeyRegistration(Guid.NewGuid(), unusual), new HotkeyRegistration(Guid.NewGuid(), unusual), new HotkeyRegistration(Guid.NewGuid(), new HotkeyGesture(HotkeyModifiers.None, 0x41))]);

        Assert.Equal([unusual], failed);
        Assert.Empty(await hotkeys.ReplaceAllAsync([]));
    }

    [Fact(SkipUnless = nameof(Enabled), Skip = SkipReason)]
    public async Task Keyboard_monitor_installs_reinstalls_and_removes_hooks()
    {
        var sink = new ForwardingSink();
        using var monitor = new KeyboardMonitor(sink, TimeProvider.System, new InputSequencerOptions(), NullLogger<KeyboardMonitor>.Instance);
        monitor.UpdateTriggers(Velotext.Core.Matching.TriggerIndex.Empty);

        Assert.False(await monitor.InvokeAsync(s => s.IsHolding));
        await monitor.ReinstallAsync();
        monitor.Post(s => s.Enabled = false);
        Assert.False(await monitor.InvokeAsync(s => s.Enabled));
    }

    [Fact(SkipUnless = nameof(Enabled), Skip = SkipReason)]
    public async Task A_read_before_the_paste_keystroke_or_by_another_program_does_not_confirm_the_paste()
    {
        using var clipboard = new ClipboardService(NullLogger<ClipboardService>.Instance);
        using var reader = new MessageLoopThread("Test reader", ThreadPriority.Normal, null, _ => { });
        var userText = await reader.InvokeAsync(() => ReadText(NativeMethods.CfUnicodeText, reader.WindowHandle, utf16: true));
        var htmlFormat = NativeMethods.RegisterClipboardFormatW("HTML Format");
        try
        {
            Assert.True(await clipboard.CopyAsync(new ClipboardPayload("original", null, null)));

            // The target reads before the paste keystroke: it gets the phrase, but the paste is not confirmed.
            var early = await clipboard.BeginPasteAsync(new ClipboardPayload("phrase", "Version:0.9\r\nx", null), (uint)Environment.ProcessId);
            Assert.NotNull(early);
            Assert.Equal("Version:0.9\r\nx", await reader.InvokeAsync(() => ReadText(htmlFormat, reader.WindowHandle, utf16: false)));
            Assert.False(early.Consumed.IsCompleted);
            await clipboard.RestoreAsync(early);
            Assert.Equal("original", await reader.InvokeAsync(() => ReadText(NativeMethods.CfUnicodeText, reader.WindowHandle, utf16: true)));

            // Another program reads after the keystroke: it is served, the paste stays unconfirmed, and it is noted.
            var other = await clipboard.BeginPasteAsync(new ClipboardPayload("phrase", null, null), targetProcessId: 4);
            Assert.NotNull(other);
            other.MarkPasteSent();
            Assert.Equal("phrase", await reader.InvokeAsync(() => ReadText(NativeMethods.CfUnicodeText, reader.WindowHandle, utf16: true)));
            Assert.False(other.Consumed.IsCompleted);
            Assert.True(other.OtherReaderSeen);
            await clipboard.RestoreAsync(other);
            Assert.Equal("original", await reader.InvokeAsync(() => ReadText(NativeMethods.CfUnicodeText, reader.WindowHandle, utf16: true)));
        }
        finally
        {
            await clipboard.CopyAsync(new ClipboardPayload(userText ?? string.Empty, null, null));
        }
    }
}
