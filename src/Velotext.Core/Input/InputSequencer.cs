// Velotext: keyboard state machine that detects triggers, holds keys during expansions and replays them in order.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Matching;

namespace Velotext.Core.Input;

/// <summary>What the keyboard hook must do with an event.</summary>
public enum KeyDecision
{
    /// <summary>Let the event through to the application.</summary>
    Pass = 0,

    /// <summary>Stop the event; Velotext handles or replays it.</summary>
    Swallow = 1,
}

/// <summary>A request to expand a matched autotext trigger.</summary>
/// <param name="OperationId">Id to pass back to <see cref="InputSequencer.CompleteOperation"/>.</param>
/// <param name="Match">The matched trigger.</param>
/// <param name="CompletingKey">The key that completed the match; it was held back and never delivered.</param>
public sealed record ExpansionRequest(long OperationId, AutotextMatch Match, KeyStroke CompletingKey);

/// <summary>What is needed to undo the last expansion.</summary>
/// <param name="PhraseId">The phrase that was inserted.</param>
/// <param name="TypedText">The trigger text exactly as typed, to type back.</param>
/// <param name="DelimiterWasResent">True when a delimiter was re-sent after the phrase; the first Backspace then just removes it.</param>
public sealed record UndoTicket(Guid PhraseId, string TypedText, bool DelimiterWasResent);

/// <summary>A request to undo the last expansion.</summary>
/// <param name="OperationId">Id to pass back to <see cref="InputSequencer.CompleteOperation"/>.</param>
/// <param name="Ticket">What to undo.</param>
public sealed record UndoRequest(long OperationId, UndoTicket Ticket);

/// <summary>How an operation ended.</summary>
/// <param name="Undo">Set when the operation can be undone with Backspace.</param>
/// <param name="SeedText">Set after an undo: the text typed back, used to restore the matching context.</param>
public sealed record OperationOutcome(UndoTicket? Undo = null, string? SeedText = null)
{
    /// <summary>An operation that cannot be undone and leaves no context.</summary>
    public static OperationOutcome None { get; } = new();
}

/// <summary>Receives the work the sequencer asks for. Calls arrive on the keyboard thread and must return quickly.</summary>
public interface IInputSequencerSink
{
    /// <summary>A trigger matched; perform the expansion, then call <see cref="InputSequencer.CompleteOperation"/>.</summary>
    /// <param name="request">The request.</param>
    void ExpansionRequested(ExpansionRequest request);

    /// <summary>The user asked to undo the last expansion; undo it, then call <see cref="InputSequencer.CompleteOperation"/>.</summary>
    /// <param name="request">The request.</param>
    void UndoRequested(UndoRequest request);

    /// <summary>Send these held-back keys again, marked as <see cref="KeyOrigin.Replay"/>, in this order.</summary>
    /// <param name="keys">The keys to replay.</param>
    void ReplayRequested(IReadOnlyList<KeyStroke> keys);

    /// <summary>
    /// Keys typed during an operation were not replayed, because the user clicked or changed windows
    /// meanwhile and the keys could land in the wrong place. Tell the user.
    /// </summary>
    /// <param name="operationId">The operation that held the keys.</param>
    /// <param name="count">Number of key presses discarded.</param>
    void HeldKeysDiscarded(long operationId, int count);
}

/// <summary>Timeouts and limits of the sequencer.</summary>
public sealed record InputSequencerOptions
{
    /// <summary>Longest an operation may hold the keyboard before keys are released anyway.</summary>
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Longest to wait for replayed keys to come back before giving up on them.</summary>
    public TimeSpan ReplayTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Most keys held back at once; beyond this, keys pass through unheld.</summary>
    public int MaxHeldKeys { get; init; } = 256;
}

/// <summary>
/// The keyboard state machine. It feeds typed characters to the <see cref="AutotextMatcher"/>, asks
/// the sink to expand matches, and while an expansion or undo runs it holds back every key the user
/// types, then replays them in their original order. Key releases of keys pressed before the hold
/// pass straight through, so modifiers are never stuck; each held key carries the modifiers the user
/// held, so the replay can restore them. Physical keys are never dropped: a replay that does not come
/// back only releases the keys waiting behind it. When the user clicks or changes windows during an
/// operation, held keys are discarded (the sink is told), because they could land in the wrong place.
/// All members must be called from the keyboard hook thread.
/// </summary>
public sealed class InputSequencer
{
    private readonly AutotextMatcher _matcher;
    private readonly IKeyTranslator _translator;
    private readonly IInputSequencerSink _sink;
    private readonly TimeProvider _time;
    private readonly InputSequencerOptions _options;

    private readonly Dictionary<long, long> _activeOperations = [];
    private readonly List<KeyStroke> _pending = [];
    private readonly List<KeyStroke> _replayRemainder = [];
    private readonly Queue<KeyStroke> _inFlight = new();
    // Keys are told apart by virtual key and the extended flag, so Enter and numpad Enter pair correctly.
    private readonly HashSet<int> _heldDowns = [];
    private readonly HashSet<int> _suppressedUps = [];

    private long _nextOperationId;
    private long _replayStarted;
    private UndoTicket? _undo;
    private bool _undoDelimiterPending;
    private bool _contextLostDuringOperation;

    /// <summary>Creates a sequencer.</summary>
    /// <param name="matcher">The autotext matcher.</param>
    /// <param name="translator">Turns key presses into text.</param>
    /// <param name="sink">Performs expansions, undos and replays.</param>
    /// <param name="time">Clock for timeouts.</param>
    /// <param name="options">Timeouts and limits; defaults when null.</param>
    public InputSequencer(AutotextMatcher matcher, IKeyTranslator translator, IInputSequencerSink sink, TimeProvider time, InputSequencerOptions? options = null)
    {
        _matcher = matcher ?? throw new ArgumentNullException(nameof(matcher));
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _options = options ?? new InputSequencerOptions();
        OperationTimeout = _options.OperationTimeout;
    }

    /// <summary>Longest an operation may hold the keyboard. Updated when the paste timeout setting changes.</summary>
    public TimeSpan OperationTimeout { get; set; }

    /// <summary>When false (paused), triggers never fire.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>When true (Velotext's own window has focus), triggers never fire.</summary>
    public bool SuppressMatching { get; set; }

    /// <summary>When true, Backspace right after an expansion undoes it.</summary>
    public bool UndoEnabled { get; set; } = true;

    /// <summary>True while keys are being held back.</summary>
    public bool IsHolding => _activeOperations.Count > 0 || _inFlight.Count > 0 || _pending.Count > 0;

    /// <summary>Number of replayed keys that never came back: the system refused them or another program swallowed them (for diagnostics).</summary>
    public int DroppedKeyCount { get; private set; }

    /// <summary>Number of held key presses discarded after a click or window change during an operation (for diagnostics).</summary>
    public int DiscardedKeyCount { get; private set; }

    /// <summary>Handles one keyboard event and decides whether it reaches the application.</summary>
    /// <param name="key">The event.</param>
    public KeyDecision OnKey(in KeyStroke key)
    {
        if (key.Origin == KeyOrigin.Own)
        {
            return KeyDecision.Pass;
        }

        var stroke = key;
        var isReturningReplay = key.Origin == KeyOrigin.Replay && _inFlight.Count > 0;
        if (isReturningReplay)
        {
            // Use the key as it was recorded, with the modifiers the user held, in case it is held again.
            var sent = _inFlight.Dequeue();
            if (sent.VirtualKey == key.VirtualKey && sent.IsKeyUp == key.IsKeyUp)
            {
                stroke = sent;
            }
        }

        var decision = ShouldHold(stroke) ? Hold(stroke) : Process(stroke);

        // Keys typed while the replay was in flight wait until the whole replay is back.
        if (isReturningReplay)
        {
            FlushIfIdle();
        }

        return decision;
    }

    /// <summary>The caret may have moved (click, focus or window change): forget the typed context.</summary>
    public void OnContextLost()
    {
        _matcher.Reset();
        _undo = null;
        if (_activeOperations.Count > 0)
        {
            _contextLostDuringOperation = true;
        }
    }

    /// <summary>Starts an operation that did not come from typing (for example a hotkey) and holds keys until it completes.</summary>
    /// <returns>The operation id to pass to <see cref="CompleteOperation"/>.</returns>
    public long BeginOperation() => StartOperation();

    /// <summary>Marks an operation as finished and releases held keys when nothing else is running.</summary>
    /// <param name="operationId">Id from the request or from <see cref="BeginOperation"/>.</param>
    /// <param name="outcome">How the operation ended.</param>
    public void CompleteOperation(long operationId, OperationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (!_activeOperations.Remove(operationId))
        {
            return; // already released by a timeout
        }

        if (outcome.SeedText is not null && !_contextLostDuringOperation)
        {
            _matcher.Seed(outcome.SeedText);
        }
        else
        {
            _matcher.Reset();
        }

        if (_activeOperations.Count == 0)
        {
            Release(operationId, outcome.Undo);
        }
    }

    /// <summary>Reports that some replayed keys could not be injected, so they will never come back.</summary>
    /// <param name="notInserted">Number of keys the system refused.</param>
    public void ReplayInjectionFailed(int notInserted)
    {
        // SendInput stops at the first refused event, so the refused keys are the newest ones.
        var refused = Math.Min(Math.Max(0, notInserted), _inFlight.Count);
        if (refused > 0)
        {
            var kept = _inFlight.Take(_inFlight.Count - refused).ToList();
            _inFlight.Clear();
            foreach (var key in kept)
            {
                _inFlight.Enqueue(key);
            }

            DroppedKeyCount += refused;
        }

        FlushIfIdle();
    }

    /// <summary>
    /// Ends every operation and replays all held keys at once, for example before the hook is removed at
    /// shutdown, so no typed key is lost.
    /// </summary>
    public void ReleaseAll()
    {
        var last = _activeOperations.Count > 0 ? _activeOperations.Keys.Max() : 0;
        _activeOperations.Clear();
        _inFlight.Clear();
        _matcher.Reset();
        Release(last, undo: null);
    }

    /// <summary>Applies timeouts. Call periodically (a few times per second) from the keyboard thread.</summary>
    public void Tick()
    {
        var now = _time.GetTimestamp();
        if (_activeOperations.Count > 0)
        {
            var expired = _activeOperations.Where(op => _time.GetElapsedTime(op.Value, now) > OperationTimeout).Select(op => op.Key).ToList();
            foreach (var id in expired)
            {
                _activeOperations.Remove(id);
            }

            if (expired.Count > 0 && _activeOperations.Count == 0)
            {
                _matcher.Reset();
                Release(expired[^1], undo: null);
            }
        }

        if (_inFlight.Count > 0 && _time.GetElapsedTime(_replayStarted, now) > _options.ReplayTimeout)
        {
            // Some replayed keys never came back (another program swallowed them, or the system blocked
            // them). They may have arrived, so they are not sent twice; the keys typed meanwhile are
            // released below instead of being held forever.
            DroppedKeyCount += _inFlight.Count;
            _inFlight.Clear();
        }

        // Safety net: never leave keys waiting when nothing is running.
        FlushIfIdle();
    }

    // Replayed keys that come back after earlier ones were held again wait behind them, so order holds.
    private bool ShouldHold(in KeyStroke key) =>
        _activeOperations.Count > 0
        || (key.Origin == KeyOrigin.Physical && (_inFlight.Count > 0 || _pending.Count > 0))
        || (key.Origin == KeyOrigin.Replay && _replayRemainder.Count > 0);

    private static int KeyId(in KeyStroke key) => key.VirtualKey | ((key.Flags & KeyStrokeAttributes.Extended) != 0 ? 0x10000 : 0);

    private KeyDecision Hold(in KeyStroke key)
    {
        var vk = KeyId(key);
        if (key.IsKeyUp)
        {
            if (_heldDowns.Remove(vk))
            {
                // Presses of this key are held (for example auto-repeat), so its release waits with them.
                _suppressedUps.Remove(vk);
            }
            else if (_suppressedUps.Remove(vk))
            {
                return KeyDecision.Swallow;
            }
            else
            {
                // A release whose press was delivered must be delivered too, or the key would look stuck.
                return KeyDecision.Pass;
            }
        }
        else
        {
            if (_pending.Count + _replayRemainder.Count >= _options.MaxHeldKeys)
            {
                return KeyDecision.Pass;
            }

            _heldDowns.Add(vk);
        }

        (key.Origin == KeyOrigin.Replay ? _replayRemainder : _pending).Add(key);
        return KeyDecision.Swallow;
    }

    private KeyDecision Process(in KeyStroke key)
    {
        var vk = KeyId(key);
        _heldDowns.Remove(vk);
        if (key.IsKeyUp)
        {
            return _suppressedUps.Remove(vk) ? KeyDecision.Swallow : KeyDecision.Pass;
        }

        // This press reaches the application (unless it starts an operation below), so its release must too.
        _suppressedUps.Remove(vk);
        var meaning = _translator.Translate(key);

        if (_undo is not null)
        {
            if (meaning.Kind == KeyMeaningKind.Backspace && UndoEnabled)
            {
                if (_undoDelimiterPending)
                {
                    _undoDelimiterPending = false;
                    return KeyDecision.Pass;
                }

                var ticket = _undo;
                _undo = null;
                var undoId = StartOperation();
                _suppressedUps.Add(vk);
                _sink.UndoRequested(new UndoRequest(undoId, ticket));
                return KeyDecision.Swallow;
            }

            if (meaning.Kind is not (KeyMeaningKind.Modifier or KeyMeaningKind.Ignore))
            {
                _undo = null;
            }
        }

        if (!Enabled || SuppressMatching)
        {
            if (meaning.Kind is not (KeyMeaningKind.Modifier or KeyMeaningKind.Ignore))
            {
                _matcher.Reset();
            }

            return KeyDecision.Pass;
        }

        switch (meaning.Kind)
        {
            case KeyMeaningKind.Text when meaning.Text is { Length: > 1 } several:
                // A key that types several characters (a dead key that does not compose, a surrogate pair)
                // never completes a trigger: the application already holds part of the text, so erasing a
                // fixed number of characters would be wrong. The buffer still keeps up.
                foreach (var c in several)
                {
                    _matcher.Feed(c);
                }

                return KeyDecision.Pass;
            case KeyMeaningKind.Text when meaning.Text is { Length: 1 } text:
                var match = _matcher.OnCharacter(text[0]);
                if (match is null)
                {
                    return KeyDecision.Pass;
                }

                var id = StartOperation();
                _suppressedUps.Add(vk);
                _matcher.Reset();
                _sink.ExpansionRequested(new ExpansionRequest(id, match, key));
                return KeyDecision.Swallow;
            case KeyMeaningKind.Backspace:
                _matcher.OnBackspace();
                return KeyDecision.Pass;
            case KeyMeaningKind.Reset:
                _matcher.Reset();
                return KeyDecision.Pass;
            default:
                return KeyDecision.Pass;
        }
    }

    private long StartOperation()
    {
        var id = ++_nextOperationId;
        _activeOperations[id] = _time.GetTimestamp();
        return id;
    }

    private void FlushIfIdle()
    {
        if (_activeOperations.Count == 0 && _inFlight.Count == 0 && (_pending.Count > 0 || _replayRemainder.Count > 0))
        {
            Flush(undo: null);
        }
    }

    /// <summary>Ends the hold after the last operation: replays the held keys, or discards them after a context loss.</summary>
    private void Release(long operationId, UndoTicket? undo)
    {
        if (!_contextLostDuringOperation)
        {
            if (_inFlight.Count > 0 && (_pending.Count > 0 || _replayRemainder.Count > 0))
            {
                // Keys of an earlier replay are still coming back; the held keys follow them in order
                // once they are back (see FlushIfIdle). Keys were typed meanwhile, so undo stays off.
                _undo = null;
                return;
            }

            Flush(undo);
            return;
        }

        _contextLostDuringOperation = false;
        _undo = null;
        var held = _replayRemainder.Concat(_pending).ToList();
        _replayRemainder.Clear();
        _pending.Clear();
        _heldDowns.Clear();

        // Modifier presses and releases still go through, so Shift, Ctrl and Alt end up in the right state.
        var modifiers = held.Where(k => k.IsModifierKey).Select(k => k.AsReplay()).ToList();
        if (modifiers.Count > 0)
        {
            StartReplay(modifiers);
        }

        var discarded = held.Count(k => !k.IsKeyUp && !k.IsModifierKey);
        if (discarded > 0)
        {
            DiscardedKeyCount += discarded;
            _sink.HeldKeysDiscarded(operationId, discarded);
        }
    }

    private void Flush(UndoTicket? undo)
    {
        if (_replayRemainder.Count == 0 && _pending.Count == 0)
        {
            _undo = UndoEnabled ? undo : null;
            _undoDelimiterPending = _undo?.DelimiterWasResent ?? false;
            return;
        }

        // The user kept typing during the operation, so Backspace no longer means "undo".
        _undo = null;
        var batch = new List<KeyStroke>(_replayRemainder.Count + _pending.Count);
        batch.AddRange(_replayRemainder.Select(k => k.AsReplay()));
        batch.AddRange(_pending.Select(k => k.AsReplay()));
        _replayRemainder.Clear();
        _pending.Clear();
        StartReplay(batch);
    }

    private void StartReplay(List<KeyStroke> batch)
    {
        foreach (var key in batch)
        {
            _inFlight.Enqueue(key);
        }

        _replayStarted = _time.GetTimestamp();
        _sink.ReplayRequested(batch);
    }
}
