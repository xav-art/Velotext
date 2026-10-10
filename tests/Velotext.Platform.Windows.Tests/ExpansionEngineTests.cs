// Velotext: tests for the expansion engine's decisions (what it erases, pastes, re-sends, undoes and reports).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Velotext.Core.Input;
using Velotext.Core.Matching;
using Velotext.Core.Model;
using Velotext.Core.RichText;
using Velotext.Platform.Windows.Expansion;
using Velotext.Platform.Windows.Interop;

namespace Velotext.Platform.Windows.Tests;

/// <summary>End-to-end engine behaviour with a real sequencer and fake Windows services.</summary>
public sealed class ExpansionEngineTests : IDisposable
{
    private static readonly TimeSpan s_wait = TimeSpan.FromSeconds(5);

    private readonly FakeHost _host = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeInjector _injector = new();
    private readonly FakeSystem _system = new();
    private readonly InputSequencer _sequencer;
    private readonly FakeSequencerHost _gateway;
    private readonly ExpansionEngine _engine;
    private readonly Phrase _formatted;
    private readonly Phrase _plain;
    private readonly Phrase _toClipboard;

    public ExpansionEngineTests()
    {
        _formatted = Phrase("Formatted", new RichDocument { Blocks = [new ListBlock { Items = [ListItem.FromText("one")] }] }, Auto("TWOX"));
        _plain = Phrase("Plain", RichDocument.FromPlainText("plain text"), Auto("brb", immediate: false), new HotkeyTrigger { Gesture = new HotkeyGesture(HotkeyModifiers.Control, 0x70) });
        _toClipboard = Phrase("Copy", RichDocument.FromPlainText("copied"), Auto("CPY")) with { Output = OutputTarget.Clipboard };
        foreach (var phrase in new[] { _formatted, _plain, _toClipboard })
        {
            _host.Phrases[phrase.Id] = phrase;
        }

        var sink = new ForwardingSink();
        var matcher = new AutotextMatcher { Index = TriggerIndex.FromLibrary(new PhraseLibrary { Items = [_formatted, _plain, _toClipboard] }) };
        _sequencer = new InputSequencer(matcher, new ScanCodeTranslator(), sink, TimeProvider.System);
        _gateway = new FakeSequencerHost(_sequencer);
        _engine = new ExpansionEngine(_host, _clipboard, _injector, _system, TimeProvider.System, NullLogger<ExpansionEngine>.Instance);
        sink.Target = _engine;
        _engine.Attach(_gateway);
    }

    public void Dispose() => _engine.Dispose();

    private static Phrase Phrase(string description, RichDocument content, params Trigger[] triggers) => new()
    {
        Id = Guid.NewGuid(),
        Description = description,
        Content = content,
        Triggers = triggers,
    };

    private static AutotextTrigger Auto(string text, bool immediate = true) => new() { Text = text, Immediate = immediate };

    private async Task<KeyDecision> Key(char c, bool up = false, int vk = 0)
    {
        var stroke = new KeyStroke(vk == 0 ? 0x1000 + c : vk, c, up ? KeyStrokeAttributes.KeyUp : KeyStrokeAttributes.None, KeyOrigin.Physical);
        return await _gateway.InvokeAsync(s => s.OnKey(stroke));
    }

    private async Task Type(string text)
    {
        foreach (var c in text)
        {
            await Key(c);
            await Key(c, up: true);
        }
    }

    private async Task WaitForCompletion() => Assert.True(await _gateway.Completed.WaitAsync(s_wait, TestContext.Current.CancellationToken));

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + s_wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Immediate_trigger_erases_typed_part_pastes_formatted_payload_and_arms_undo()
    {
        await Type("TWOX");
        await WaitForCompletion();

        Assert.Equal(["backspace x3", "chord 11+56"], _injector.Calls);
        var payload = Assert.Single(_clipboard.Pasted);
        Assert.NotNull(payload.Html);
        Assert.NotNull(payload.Rtf);
        Assert.Equal("• one", payload.Text);
        Assert.Equal(1, _clipboard.Restores);
        Assert.Equal([_formatted.Id], _host.Inserted);

        // Backspace right away undoes: Ctrl+Z, then the trigger is typed back.
        Assert.Equal(KeyDecision.Swallow, await Key('\b', vk: 0x08));
        await WaitForCompletion();
        Assert.Equal(["backspace x3", "chord 11+56", "chord 11+5A", "type 'TWOX'"], _injector.Calls);
    }

    [Fact]
    public async Task Delimiter_trigger_resends_delimiter_and_plain_phrase_pastes_text_only()
    {
        await Type("brb ");
        await WaitForCompletion();

        Assert.Equal(["backspace x3", "chord 11+56", "resend  "], _injector.Calls);
        var payload = Assert.Single(_clipboard.Pasted);
        Assert.Null(payload.Html);
        Assert.Null(payload.Rtf);
        Assert.Equal("plain text", payload.Text);
    }

    [Fact]
    public async Task Clipboard_target_copies_and_notifies()
    {
        await Type("CPY");
        await WaitForCompletion();

        Assert.Equal(["backspace x2"], _injector.Calls);
        Assert.Equal("copied", Assert.Single(_clipboard.Copied).Text);
        Assert.Empty(_clipboard.Pasted);
        Assert.Equal(ExpansionNoticeKind.CopiedToClipboard, Assert.Single(_host.Notices).Kind);

        // No undo for the clipboard target.
        Assert.Equal(KeyDecision.Pass, await Key('\b', vk: 0x08));
    }

    [Fact]
    public async Task Clipboard_target_failure_puts_trigger_back()
    {
        _clipboard.CopySucceeds = false;
        await Type("CPY");
        await WaitForCompletion();

        Assert.Equal(["backspace x2", "type 'CP'", "resend Y"], _injector.Calls);
        Assert.Equal(ExpansionNoticeKind.ClipboardBusy, Assert.Single(_host.Notices).Kind);
    }

    [Fact]
    public async Task Busy_clipboard_puts_the_trigger_back_as_typed()
    {
        _clipboard.Mode = FakeClipboard.Behaviour.Busy;
        await Type("TWOX");
        await WaitForCompletion();

        Assert.Equal(["backspace x3", "type 'TWO'", "resend X"], _injector.Calls);
        Assert.Equal(ExpansionNoticeKind.ClipboardBusy, Assert.Single(_host.Notices).Kind);
        Assert.Empty(_host.Inserted);
    }

    [Fact]
    public async Task Unconfirmed_paste_warns_and_does_not_arm_undo()
    {
        _clipboard.Mode = FakeClipboard.Behaviour.Ignore;
        await Type("TWOX");
        await WaitForCompletion();

        Assert.Equal(ExpansionNoticeKind.PasteNotConfirmed, Assert.Single(_host.Notices).Kind);
        Assert.Equal(KeyDecision.Pass, await Key('\b', vk: 0x08));

        // The phrase stays on the clipboard for a late read; leaving the target gives the clipboard back.
        Assert.Equal(0, _clipboard.Restores);
        _system.Target = new ForegroundTarget(200, 99, 98);
        await WaitUntil(() => _clipboard.Restores == 1);
    }

    [Fact]
    public async Task Replaced_clipboard_is_not_restored_by_the_engine_logic_and_undo_stays_off()
    {
        _clipboard.Mode = FakeClipboard.Behaviour.Replace;
        await Type("TWOX");
        await WaitForCompletion();

        Assert.Empty(_host.Notices);
        Assert.Equal(KeyDecision.Pass, await Key('\b', vk: 0x08));
    }

    [Fact]
    public async Task Undo_setting_off_lets_backspace_through()
    {
        _host.Settings = _host.Settings with { UndoWithBackspace = false };
        await Type("TWOX");
        await WaitForCompletion();

        Assert.Equal(KeyDecision.Pass, await Key('\b', vk: 0x08));
    }

    [Fact]
    public async Task Deleted_phrase_delivers_the_held_key()
    {
        _host.Phrases.Clear();
        await Type("TWOX");
        await WaitForCompletion();

        Assert.Equal(["resend X"], _injector.Calls);
    }

    [Fact]
    public async Task Refused_backspaces_deliver_the_held_key_and_paste_nothing()
    {
        _injector.Accept = false;
        await Type("TWOX");
        await WaitForCompletion();

        Assert.Equal(["backspace x3", "resend X"], _injector.Calls);
        Assert.Empty(_clipboard.Pasted);
    }

    [Fact]
    public async Task Held_modifiers_are_released_after_a_second()
    {
        _system.HeldKeys[0xA0] = true;
        await Type("TWOX");
        await WaitForCompletion();

        Assert.Equal("release A0", _injector.Calls.First());
    }

    [Fact]
    public async Task Keys_typed_during_expansion_are_replayed_and_refusals_reported()
    {
        _injector.RefuseReplay = 1;
        _host.Settings = _host.Settings with { PasteTimeout = TimeSpan.FromMilliseconds(500) };
        _clipboard.Mode = FakeClipboard.Behaviour.Ignore;
        await Type("TWOX");
        await Key('a');
        await WaitForCompletion();
        await WaitForCompletion();

        Assert.Contains("replay 1", _injector.Calls);
        Assert.Equal(1, _sequencer.DroppedKeyCount);
        Assert.False(_sequencer.IsHolding);
    }

    [Fact]
    public async Task Hotkey_inserts_without_erasing_and_respects_pause_and_own_window()
    {
        await _engine.InsertFromHotkeyAsync(_plain.Id);
        await WaitForCompletion();
        Assert.Equal(["chord 11+56"], _injector.Calls);

        _sequencer.Enabled = false;
        await _engine.InsertFromHotkeyAsync(_plain.Id);
        _sequencer.Enabled = true;
        _system.Target = new ForegroundTarget(1, (uint)Environment.ProcessId, 1);
        await _engine.InsertFromHotkeyAsync(_plain.Id);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Single(_clipboard.Pasted);
    }

    [Fact]
    public async Task Hotkey_for_clipboard_target_copies()
    {
        await _engine.InsertFromHotkeyAsync(_toClipboard.Id);
        await WaitForCompletion();

        Assert.Single(_clipboard.Copied);
        Assert.Equal(ExpansionNoticeKind.CopiedToClipboard, Assert.Single(_host.Notices).Kind);
    }

    [Fact]
    public async Task Hotkey_with_busy_clipboard_or_missing_phrase_reports_or_ignores()
    {
        _clipboard.Mode = FakeClipboard.Behaviour.Busy;
        await _engine.InsertFromHotkeyAsync(_plain.Id);
        await WaitForCompletion();
        Assert.Equal(ExpansionNoticeKind.ClipboardBusy, Assert.Single(_host.Notices).Kind);

        await _engine.InsertFromHotkeyAsync(Guid.NewGuid());
        await WaitForCompletion();
        Assert.Empty(_clipboard.Pasted);
    }

    [Fact]
    public void Payload_builder_uses_plain_text_only_for_plain_phrases()
    {
        var plain = ExpansionEngine.BuildPayload(RichDocument.FromPlainText("a\nb"), RtfOptions.Default);
        var rich = ExpansionEngine.BuildPayload(new RichDocument { Blocks = [new Paragraph { Inlines = [new TextRun { Text = "b", Style = TextStyle.Bold }] }] }, RtfOptions.Default);

        Assert.Equal("a\r\nb", plain.Text);
        Assert.Null(plain.Html);
        Assert.StartsWith("Version:0.9", rich.Html, StringComparison.Ordinal);
        Assert.StartsWith("{\\rtf1", rich.Rtf, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Click_before_the_backspaces_stops_without_typing_and_reports_once()
    {
        _system.HeldKeys[0xA0] = true;
        await Type("TWOX");
        await Key('a');
        _gateway.Click();
        _system.HeldKeys.TryRemove(0xA0, out _);
        await WaitForCompletion();

        // Nothing is typed into the place the user left; one notice covers the phrase and the dropped key.
        Assert.DoesNotContain(_injector.Calls, call => call.StartsWith("backspace", StringComparison.Ordinal));
        Assert.Empty(_clipboard.Pasted);
        Assert.Equal(ExpansionNoticeKind.WindowChanged, Assert.Single(_host.Notices).Kind);
        Assert.Equal(1, _sequencer.DiscardedKeyCount);
    }

    [Fact]
    public async Task Window_change_before_the_backspaces_stops_the_expansion()
    {
        _system.HeldKeys[0xA0] = true;
        await Type("TWOX");
        _system.Target = new ForegroundTarget(200, 99, 98);
        _system.HeldKeys.TryRemove(0xA0, out _);
        await WaitForCompletion();

        Assert.Empty(_clipboard.Pasted);
        Assert.Equal(ExpansionNoticeKind.WindowChanged, Assert.Single(_host.Notices).Kind);
    }

    [Fact]
    public async Task Click_after_the_paste_drops_the_keys_typed_meanwhile_with_a_notice()
    {
        await Type("TWOX");
        await WaitUntil(() => _injector.Calls.Contains("chord 11+56"));
        await Key('a');
        _gateway.Click();
        await WaitForCompletion();

        var notice = Assert.Single(_host.Notices);
        Assert.Equal(ExpansionNoticeKind.KeysDiscarded, notice.Kind);
        Assert.Equal(1, notice.KeyCount);
        Assert.Equal([_formatted.Id], _host.Inserted);
        Assert.DoesNotContain(_injector.Calls, call => call.StartsWith("replay", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unconfirmed_paste_does_not_resend_the_delimiter()
    {
        _clipboard.Mode = FakeClipboard.Behaviour.Ignore;
        await Type("brb ");
        await WaitForCompletion();

        // The delimiter can be Enter, which would send a chat message with unknown content.
        Assert.Equal(["backspace x3", "chord 11+56"], _injector.Calls);
        Assert.Equal(ExpansionNoticeKind.PasteNotConfirmed, Assert.Single(_host.Notices).Kind);
    }

    [Fact]
    public async Task Delimiter_triggers_and_terminals_do_not_arm_undo()
    {
        await Type("brb ");
        await WaitForCompletion();
        Assert.Equal(KeyDecision.Pass, await Key('\b', vk: 0x08));

        _system.Terminals.Add(_system.Target.Window);
        await Type(" TWOX");
        await WaitForCompletion();
        Assert.Equal(KeyDecision.Pass, await Key('\b', vk: 0x08));
    }

    [Fact]
    public void Replays_after_shutdown_are_sent_at_once()
    {
        _engine.Dispose();

        _engine.ReplayRequested([new KeyStroke(0x41, 'a', KeyStrokeAttributes.None, KeyOrigin.Replay)]);

        Assert.Contains("replay 1", _injector.Calls);
    }
}
