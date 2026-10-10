// Velotext: tests for the keyboard state machine (holding, ordered replay, undo, timeouts).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Input;
using Velotext.Core.Matching;

namespace Velotext.Core.Tests.Input;

/// <summary>Behaviour of <see cref="InputSequencer"/>: it must never lose, reorder or stick a key.</summary>
public sealed class InputSequencerTests
{
    private readonly RecordingSink _sink = new();
    private readonly ManualTime _time = new();
    private readonly AutotextMatcher _matcher = new();
    private readonly InputSequencer _sequencer;

    public InputSequencerTests()
    {
        _matcher.Index = Build.Index(Build.Auto("DEUX"), Build.Auto("brb", immediate: false));
        _sequencer = new InputSequencer(_matcher, new FakeTranslator(), _sink, _time, new InputSequencerOptions { MaxHeldKeys = 32 });
    }

    private KeyDecision Tap(char c, KeyOrigin origin = KeyOrigin.Physical)
    {
        var down = _sequencer.OnKey(Keys.Down(c, origin));
        _sequencer.OnKey(Keys.Up(c, origin));
        return down;
    }

    private void TypeText(string text)
    {
        foreach (var c in text)
        {
            Tap(c);
        }
    }

    private void Feed(IEnumerable<KeyStroke> keys)
    {
        foreach (var key in keys)
        {
            _sequencer.OnKey(key);
        }
    }

    [Fact]
    public void Completing_key_is_swallowed_and_expansion_requested()
    {
        Assert.Equal(KeyDecision.Pass, Tap('D'));
        Assert.Equal(KeyDecision.Pass, Tap('E'));
        Assert.Equal(KeyDecision.Pass, Tap('U'));

        var down = _sequencer.OnKey(Keys.Down('X'));
        var up = _sequencer.OnKey(Keys.Up('X'));

        Assert.Equal(KeyDecision.Swallow, down);
        Assert.Equal(KeyDecision.Swallow, up);
        var request = Assert.Single(_sink.Expansions);
        Assert.Equal("DEUX", request.Match.TypedText);
        Assert.Equal('X', (char)request.CompletingKey.ScanCode);
        Assert.True(_sequencer.IsHolding);
    }

    [Fact]
    public void Keys_typed_during_expansion_are_held_and_replayed_in_order()
    {
        TypeText("DEU");
        _sequencer.OnKey(Keys.Down('X'));

        Assert.Equal(KeyDecision.Swallow, _sequencer.OnKey(Keys.Down('a')));
        Assert.Equal(KeyDecision.Swallow, _sequencer.OnKey(Keys.Up('a')));
        Assert.Equal(KeyDecision.Swallow, _sequencer.OnKey(Keys.Down('b')));

        _sequencer.CompleteOperation(_sink.Expansions[0].OperationId, OperationOutcome.None);

        var replay = Assert.Single(_sink.Replays);
        Assert.Equal(['a', 'a', 'b'], replay.Select(k => (char)k.ScanCode));
        Assert.All(replay, k => Assert.Equal(KeyOrigin.Replay, k.Origin));

        // A physical key typed before the replay arrives waits behind it.
        Assert.Equal(KeyDecision.Swallow, _sequencer.OnKey(Keys.Down('c')));

        // Replayed keys come back and pass through.
        foreach (var key in replay)
        {
            Assert.Equal(KeyDecision.Pass, _sequencer.OnKey(key));
        }

        // Then the waiting physical key is replayed.
        Assert.Equal(2, _sink.Replays.Count);
        Assert.Equal('c', (char)_sink.Replays[1][0].ScanCode);
    }

    [Fact]
    public void Release_of_key_pressed_before_hold_passes_through()
    {
        _sequencer.OnKey(Keys.DownVk(FakeTranslator.Shift));
        TypeText("DEU");
        _sequencer.OnKey(Keys.Down('X'));

        Assert.Equal(KeyDecision.Pass, _sequencer.OnKey(Keys.UpVk(FakeTranslator.Shift)));
    }

    [Fact]
    public void Operation_without_held_keys_does_not_replay()
    {
        TypeText("DEUX");
        _sequencer.CompleteOperation(_sink.Expansions[0].OperationId, OperationOutcome.None);

        Assert.Empty(_sink.Replays);
        Assert.False(_sequencer.IsHolding);
        Assert.Equal(KeyDecision.Pass, Tap('a'));
    }

    [Fact]
    public void Backspace_after_expansion_requests_undo()
    {
        TypeText("DEUX");
        var ticket = new UndoTicket(Guid.NewGuid(), "DEUX", DelimiterWasResent: false);
        _sequencer.CompleteOperation(_sink.Expansions[0].OperationId, new OperationOutcome(Undo: ticket));

        var down = _sequencer.OnKey(Keys.DownVk(FakeTranslator.Backspace));
        var up = _sequencer.OnKey(Keys.UpVk(FakeTranslator.Backspace));

        Assert.Equal(KeyDecision.Swallow, down);
        Assert.Equal(KeyDecision.Swallow, up);
        Assert.Same(ticket, Assert.Single(_sink.Undos).Ticket);
    }

    [Fact]
    public void Undo_restores_matching_context_from_seed()
    {
        TypeText("brb ");
        var ticket = new UndoTicket(Guid.NewGuid(), "brb", DelimiterWasResent: true);
        _sequencer.CompleteOperation(_sink.Expansions[0].OperationId, new OperationOutcome(Undo: ticket));

        // First Backspace removes the re-sent delimiter, the second one undoes.
        Assert.Equal(KeyDecision.Pass, _sequencer.OnKey(Keys.DownVk(FakeTranslator.Backspace)));
        _sequencer.OnKey(Keys.UpVk(FakeTranslator.Backspace));
        Assert.Equal(KeyDecision.Swallow, _sequencer.OnKey(Keys.DownVk(FakeTranslator.Backspace)));
        _sequencer.OnKey(Keys.UpVk(FakeTranslator.Backspace));

        _sequencer.CompleteOperation(_sink.Undos[0].OperationId, new OperationOutcome(SeedText: "brb"));

        // The restored trigger does not fire again on the next space.
        Assert.Equal(KeyDecision.Pass, Tap(' '));
        Assert.Single(_sink.Expansions);
    }

    [Fact]
    public void Modifiers_and_ignored_keys_keep_undo_armed()
    {
        TypeText("DEUX");
        var ticket = new UndoTicket(Guid.NewGuid(), "DEUX", DelimiterWasResent: false);
        _sequencer.CompleteOperation(_sink.Expansions[0].OperationId, new OperationOutcome(Undo: ticket));

        _sequencer.OnKey(Keys.DownVk(FakeTranslator.Shift));
        _sequencer.OnKey(Keys.UpVk(FakeTranslator.Shift));
        _sequencer.OnKey(Keys.DownVk(0xAD));

        Assert.Equal(KeyDecision.Swallow, _sequencer.OnKey(Keys.DownVk(FakeTranslator.Backspace)));
        Assert.Single(_sink.Undos);
    }

    [Fact]
    public void Typing_after_expansion_disarms_undo()
    {
        TypeText("DEUX");
        var ticket = new UndoTicket(Guid.NewGuid(), "DEUX", DelimiterWasResent: false);
        _sequencer.CompleteOperation(_sink.Expansions[0].OperationId, new OperationOutcome(Undo: ticket));

        Tap('a');

        Assert.Equal(KeyDecision.Pass, _sequencer.OnKey(Keys.DownVk(FakeTranslator.Backspace)));
        Assert.Empty(_sink.Undos);
    }

    [Fact]
    public void Undo_disabled_lets_backspace_through()
    {
        _sequencer.UndoEnabled = false;
        TypeText("DEUX");
        _sequencer.CompleteOperation(_sink.Expansions[0].OperationId, new OperationOutcome(new UndoTicket(Guid.NewGuid(), "DEUX", false)));

        Assert.Equal(KeyDecision.Pass, _sequencer.OnKey(Keys.DownVk(FakeTranslator.Backspace)));
        Assert.Empty(_sink.Undos);
    }

    [Fact]
    public void Undo_is_not_armed_when_user_kept_typing_during_expansion()
    {
        TypeText("DEU");
        _sequencer.OnKey(Keys.Down('X'));
        _sequencer.OnKey(Keys.Down('z'));
        _sequencer.CompleteOperation(_sink.Expansions[0].OperationId, new OperationOutcome(new UndoTicket(Guid.NewGuid(), "DEUX", false)));
        Feed(_sink.Replays[0]);

        Assert.Equal(KeyDecision.Pass, _sequencer.OnKey(Keys.DownVk(FakeTranslator.Backspace)));
        Assert.Empty(_sink.Undos);
    }

    [Fact]
    public void Context_loss_disarms_undo_and_forgets_partial_trigger()
    {
        TypeText("DEUX");
        _sequencer.CompleteOperation(_sink.Expansions[0].OperationId, new OperationOutcome(new UndoTicket(Guid.NewGuid(), "DEUX", false)));
        _sequencer.OnContextLost();
        Assert.Equal(KeyDecision.Pass, _sequencer.OnKey(Keys.DownVk(FakeTranslator.Backspace)));

        TypeText("DE");
        _sequencer.OnContextLost();
        TypeText("UX");
        Assert.Single(_sink.Expansions);
    }

    [Fact]
    public void Own_keys_pass_and_are_ignored()
    {
        foreach (var c in "DEUX")
        {
            Assert.Equal(KeyDecision.Pass, _sequencer.OnKey(Keys.Down(c, KeyOrigin.Own)));
        }

        Assert.Empty(_sink.Expansions);
    }

    [Fact]
    public void Paused_or_own_window_never_expands()
    {
        _sequencer.Enabled = false;
        TypeText("DEUX");
        _sequencer.Enabled = true;
        _sequencer.SuppressMatching = true;
        TypeText(" DEUX");
        _sequencer.SuppressMatching = false;
        TypeText("X");

        Assert.Empty(_sink.Expansions);
    }

    [Fact]
    public void Navigation_key_resets_and_backspace_edits()
    {
        TypeText("DE");
        _sequencer.OnKey(Keys.DownVk(FakeTranslator.Left));
        TypeText("UX");
        Assert.Empty(_sink.Expansions);

        TypeText(" DEUZ");
        _sequencer.OnKey(Keys.DownVk(FakeTranslator.Backspace));
        _sequencer.OnKey(Keys.DownVk(FakeTranslator.Dead));
        TypeText("X");
        Assert.Single(_sink.Expansions);
    }

    [Fact]
    public void Operation_timeout_releases_held_keys()
    {
        TypeText("DEUX");
        _sequencer.OnKey(Keys.Down('a'));

        _time.Advance(TimeSpan.FromSeconds(4));
        _sequencer.Tick();
        Assert.Empty(_sink.Replays);

        _time.Advance(TimeSpan.FromSeconds(2));
        _sequencer.Tick();
        Assert.Single(_sink.Replays);

        // The late completion is ignored.
        _sequencer.CompleteOperation(_sink.Expansions[0].OperationId, OperationOutcome.None);
        Assert.Single(_sink.Replays);
    }

    [Fact]
    public void Replay_timeout_drops_keys_that_never_came_back()
    {
        TypeText("DEUX");
        _sequencer.OnKey(Keys.Down('a'));
        _sequencer.CompleteOperation(_sink.Expansions[0].OperationId, OperationOutcome.None);
        _sequencer.OnKey(Keys.Down('b'));

        _time.Advance(TimeSpan.FromSeconds(3));
        _sequencer.Tick();

        Assert.Equal(1, _sequencer.DroppedKeyCount);
        Assert.False(_sequencer.IsHolding);
        Assert.Equal(KeyDecision.Pass, Tap('c'));
    }

    [Fact]
    public void Failed_replay_injection_releases_the_keyboard()
    {
        TypeText("DEUX");
        _sequencer.OnKey(Keys.Down('a'));
        _sequencer.CompleteOperation(_sink.Expansions[0].OperationId, OperationOutcome.None);

        _sequencer.ReplayInjectionFailed(1);

        Assert.False(_sequencer.IsHolding);
        Assert.Equal(KeyDecision.Pass, Tap('c'));
    }

    [Fact]
    public void Trigger_inside_replay_keeps_older_replayed_keys_before_newer_physical_keys()
    {
        TypeText("DEUX");
        TypeText(" DEUX!");   // held: ' ', D, E, U, X, !
        _sequencer.CompleteOperation(_sink.Expansions[0].OperationId, OperationOutcome.None);
        var replay = _sink.Replays[0];

        _sequencer.OnKey(Keys.Down('p')); // newer physical key, waits

        // Replay " DEU" passes, "X" fires the second expansion, and the rest of the replay is held again.
        Feed(replay);
        Assert.Equal(2, _sink.Expansions.Count);

        _sequencer.CompleteOperation(_sink.Expansions[1].OperationId, OperationOutcome.None);

        var second = _sink.Replays[1].Where(k => !k.IsKeyUp).Select(k => (char)k.ScanCode);
        Assert.Equal(['!', 'p'], second);
    }

    [Fact]
    public void Begin_operation_holds_keys_for_hotkeys()
    {
        var id = _sequencer.BeginOperation();
        Assert.Equal(KeyDecision.Swallow, _sequencer.OnKey(Keys.Down('a')));

        _sequencer.CompleteOperation(id, OperationOutcome.None);

        Assert.Single(_sink.Replays);
    }

    [Fact]
    public void Too_many_held_keys_pass_through()
    {
        var id = _sequencer.BeginOperation();
        for (var i = 0; i < 32; i++)
        {
            Assert.Equal(KeyDecision.Swallow, _sequencer.OnKey(Keys.Down((char)('A' + i))));
        }

        Assert.Equal(KeyDecision.Pass, _sequencer.OnKey(Keys.Down('z')));
        Assert.Equal(KeyDecision.Pass, _sequencer.OnKey(Keys.Up('z')));
        _sequencer.CompleteOperation(id, OperationOutcome.None);
    }

    [Fact]
    public void Unknown_operation_completion_is_ignored()
    {
        _sequencer.CompleteOperation(42, OperationOutcome.None);
        Assert.False(_sequencer.IsHolding);
        Assert.Throws<ArgumentNullException>(() => _sequencer.CompleteOperation(1, null!));
    }

    [Fact]
    public void Constructor_checks_arguments()
    {
        var translator = new FakeTranslator();
        Assert.Throws<ArgumentNullException>(() => new InputSequencer(null!, translator, _sink, _time));
        Assert.Throws<ArgumentNullException>(() => new InputSequencer(_matcher, null!, _sink, _time));
        Assert.Throws<ArgumentNullException>(() => new InputSequencer(_matcher, translator, null!, _time));
        Assert.Throws<ArgumentNullException>(() => new InputSequencer(_matcher, translator, _sink, null!));
    }

    [Fact]
    public void Key_meaning_factories_carry_kind()
    {
        Assert.Equal(KeyMeaningKind.Text, KeyMeaning.ForText("a").Kind);
        Assert.Equal("a", KeyMeaning.ForText("a").Text);
        Assert.Equal(KeyMeaningKind.Ignore, KeyMeaning.Ignore.Kind);
        Assert.Equal(KeyMeaningKind.Reset, KeyMeaning.Reset.Kind);
        Assert.Equal(KeyMeaningKind.DeadKey, KeyMeaning.DeadKey.Kind);
    }
}
