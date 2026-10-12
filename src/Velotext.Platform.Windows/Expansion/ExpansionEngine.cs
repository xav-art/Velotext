// Velotext: performs expansions, undos, hotkey insertions and key replays, one at a time, off the hook thread.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Velotext.Core.Input;
using Velotext.Core.Model;
using Velotext.Core.RichText;
using Velotext.Core.Usage;
using Velotext.Platform.Windows.Clipboard;
using Velotext.Platform.Windows.Input;
using Velotext.Platform.Windows.Interop;

namespace Velotext.Platform.Windows.Expansion;

/// <summary>Settings the engine reads for every operation.</summary>
/// <param name="UndoWithBackspace">Arm undo after a successful paste.</param>
/// <param name="PasteTimeout">Longest wait for the target to read the pasted phrase.</param>
/// <param name="Rtf">Font used for RTF output.</param>
public sealed record ExpansionSettings(bool UndoWithBackspace, TimeSpan PasteTimeout, RtfOptions Rtf);

/// <summary>Kinds of user-facing notices raised by the engine.</summary>
public enum ExpansionNoticeKind
{
    /// <summary>A phrase went to the clipboard (Clipboard output target).</summary>
    CopiedToClipboard = 0,

    /// <summary>The target application did not confirm that it read the paste.</summary>
    PasteNotConfirmed = 1,

    /// <summary>The clipboard was held by another application; nothing was inserted.</summary>
    ClipboardBusy = 2,

    /// <summary>The user clicked or changed windows before the phrase was inserted; Velotext stopped.</summary>
    WindowChanged = 3,

    /// <summary>The user clicked or changed windows during an insertion; keys typed meanwhile were not replayed.</summary>
    KeysDiscarded = 4,

    /// <summary>The insertion took too long (for example a hung clipboard owner); Velotext stopped it.</summary>
    Expired = 5,

    /// <summary>The phrase was inserted, but the user left before Velotext could re-send the key after the trigger.</summary>
    DelimiterSkipped = 6,

    /// <summary>The app has not taken the previous phrase yet, so this one was not inserted.</summary>
    TargetBusy = 7,
}

/// <summary>A notice for the user.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="PhraseDescription">The phrase involved (empty for <see cref="ExpansionNoticeKind.KeysDiscarded"/>).</param>
/// <param name="TriggerRestored">True when Velotext typed the user's trigger back (typed-text triggers only).</param>
/// <param name="KeyCount">For <see cref="ExpansionNoticeKind.KeysDiscarded"/>: how many key presses were not replayed.</param>
public sealed record ExpansionNotice(ExpansionNoticeKind Kind, string PhraseDescription, bool TriggerRestored = false, int KeyCount = 0);

/// <summary>What the engine needs from the application.</summary>
public interface IExpansionHost
{
    /// <summary>Current expansion settings.</summary>
    ExpansionSettings Settings { get; }

    /// <summary>Finds a phrase in the current library snapshot.</summary>
    /// <param name="id">Phrase id.</param>
    Phrase? FindPhrase(Guid id);

    /// <summary>Called after a phrase was inserted or copied, for "Recently used", "Most used" and the usage dashboard.</summary>
    /// <param name="id">Phrase id.</param>
    /// <param name="typedCharacters">Characters the user typed to trigger it: the trigger text, or one for a hotkey.</param>
    void PhraseInserted(Guid id, int typedCharacters);

    /// <summary>Shows a notice to the user. Called from a background thread.</summary>
    /// <param name="notice">The notice.</param>
    void Notify(ExpansionNotice notice);
}

/// <summary>
/// Executes the work the keyboard sequencer asks for, strictly one item at a time on a background
/// task, so expansions never overlap and the hook thread stays free. Every operation reports back
/// to the sequencer when it ends, which releases the keys typed meanwhile. Replays of held keys run on
/// their own task, so they are never stuck behind a slow operation; an operation that the sequencer
/// gave up on (it took too long) stops before it types anything more.
/// </summary>
public sealed partial class ExpansionEngine : IInputSequencerSink, IDisposable
{
    private static readonly TimeSpan s_modifierWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_minimumHold = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan s_readerGrace = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_readerCloseWait = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan s_lateReadWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan s_lateReadPoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_assumedHold = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan s_pastePoll = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan s_targetIdleWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_restoreHold = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_firstPasteRestoreHold = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_undoModifierHold = TimeSpan.FromMilliseconds(150);
    private static readonly int[] s_modifierKeys = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];

    private readonly IExpansionHost _host;
    private readonly IClipboardService _clipboard;
    private readonly IInputInjector _injector;
    private readonly ISystemState _system;
    private readonly TimeProvider _time;
    private readonly ILogger<ExpansionEngine> _logger;
    private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<IReadOnlyList<KeyStroke>> _replays = Channel.CreateUnbounded<IReadOnlyList<KeyStroke>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<long, bool> _expired = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private readonly Task _replayWorker;
    private ISequencerHost? _monitor;
    private Task _lateRestore = Task.CompletedTask;
    private Task _heldRestore = Task.CompletedTask;
    private readonly ConcurrentDictionary<uint, bool> _pastedProcesses = new();
    private long _abandonedOperation;
    private int _disposed;

    /// <summary>Creates the engine and starts its worker.</summary>
    /// <param name="host">Application services.</param>
    /// <param name="clipboard">Clipboard access.</param>
    /// <param name="injector">Input injection.</param>
    /// <param name="system">Foreground, key and clipboard state.</param>
    /// <param name="time">Clock.</param>
    /// <param name="logger">Diagnostics logger. Never receives typed text or phrase content.</param>
    public ExpansionEngine(IExpansionHost host, IClipboardService clipboard, IInputInjector injector, ISystemState system, TimeProvider time, ILogger<ExpansionEngine> logger)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _injector = injector ?? throw new ArgumentNullException(nameof(injector));
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _worker = Task.Run(RunAsync);
        _replayWorker = Task.Run(ReplayAsync);
    }

    /// <summary>Connects the keyboard monitor that receives operation results. Call once, before typing starts.</summary>
    /// <param name="monitor">The keyboard monitor using this engine as its sink.</param>
    public void Attach(ISequencerHost monitor) => _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));

    /// <inheritdoc />
    /// <remarks>Runs on the keyboard thread at the moment of the match, so the window that received the trigger is recorded exactly.</remarks>
    public void ExpansionRequested(ExpansionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_work.Writer.TryWrite(new ExpandWork(request, CurrentPlace(request.OperationId))))
        {
            // Shutting down: deliver the held-back key and end the operation instead of losing the key.
            var key = request.CompletingKey;
            var physical = Physical();
            _ = Task.Run(() => _injector.Resend(key, physical));
            _monitor?.Post(sequencer => sequencer.CompleteOperation(request.OperationId, OperationOutcome.None));
        }
    }

    /// <inheritdoc />
    public void UndoRequested(UndoRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_work.Writer.TryWrite(new UndoWork(request, CurrentPlace(request.OperationId))))
        {
            _monitor?.Post(sequencer => sequencer.CompleteOperation(request.OperationId, OperationOutcome.None));
        }
    }

    /// <inheritdoc />
    public void ReplayRequested(IReadOnlyList<KeyStroke> keys)
    {
        if (!_replays.Writer.TryWrite(keys))
        {
            // Shutting down: deliver the user's keys now rather than lose them.
            _injector.Replay(keys, Physical());
        }
    }

    /// <inheritdoc />
    /// <remarks>Runs on the keyboard thread; the operation's next injection check sees it.</remarks>
    public void OperationExpired(long operationId) => _expired[operationId] = true;

    /// <inheritdoc />
    public void HeldKeysDiscarded(long operationId, int count)
    {
        LogDiscarded(_logger, operationId, count);
        // When the operation already stopped with a "phrase not inserted" notice, that notice covers it.
        if (operationId != Interlocked.Read(ref _abandonedOperation))
        {
            _host.Notify(new ExpansionNotice(ExpansionNoticeKind.KeysDiscarded, string.Empty, KeyCount: count));
        }
    }

    /// <summary>Inserts a phrase because its hotkey was pressed. Ignored while paused or inside Velotext.</summary>
    /// <param name="phraseId">The phrase to insert.</param>
    public async Task InsertFromHotkeyAsync(Guid phraseId)
    {
        var place = CurrentPlace(0);
        if (_monitor is null || place.Target.IsOwnProcess)
        {
            return;
        }

        // A hotkey press while an operation runs (for example caused by Velotext's own keystrokes) is ignored.
        var operationId = await _monitor.InvokeAsync(sequencer => sequencer.Enabled && !sequencer.IsHolding ? sequencer.BeginOperation() : 0).ConfigureAwait(false);
        if (operationId != 0 && !_work.Writer.TryWrite(new HotkeyWork(operationId, phraseId, place with { OperationId = operationId })))
        {
            _monitor.Post(sequencer => sequencer.CompleteOperation(operationId, OperationOutcome.None));
        }
    }

    /// <summary>Builds the clipboard formats for a phrase. Plain phrases get plain text only, so the target applies its own style.</summary>
    /// <param name="content">The phrase content.</param>
    /// <param name="rtf">RTF font settings.</param>
    public static ClipboardPayload BuildPayload(RichDocument content, RtfOptions rtf)
    {
        ArgumentNullException.ThrowIfNull(content);
        var text = PlainTextRenderer.Render(content);
        return content.HasFormatting
            ? new ClipboardPayload(text, HtmlRenderer.ToClipboardFormat(HtmlRenderer.RenderFragment(content)), RtfRenderer.Render(content, rtf))
            : new ClipboardPayload(text, null, null);
    }

    /// <summary>Stops the worker after the current operation.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _work.Writer.TryComplete();
        _replays.Writer.TryComplete();
        _stop.Cancel();
        try
        {
            // Cancelled operations and late restores give the user's clipboard back before they end;
            // queued replays still go out.
            Task.WaitAll([_worker, _replayWorker, _lateRestore, _heldRestore], TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Cancellation during shutdown.
        }

        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var item in _work.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                await ExecuteAsync(item).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    private async Task ReplayAsync()
    {
        // Not cancelled by shutdown: replays queued before it still reach the application.
        await foreach (var keys in _replays.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var physical = Physical();
                var refused = _injector.Replay(keys, physical);
                LogReplay(_logger, keys.Count, refused, keys.Count(k => k.IsModifierKey), physical);
                if (refused > 0)
                {
                    _monitor?.Post(sequencer => sequencer.ReplayInjectionFailed(refused));
                }
            }
            catch (Exception ex)
            {
                LogOperationFailed(_logger, "Replay", ex);
            }
        }
    }

    private async Task ExecuteAsync(Work item)
    {
        var operationId = item switch
        {
            ExpandWork expand => expand.Request.OperationId,
            UndoWork undo => undo.Request.OperationId,
            HotkeyWork hotkey => hotkey.OperationId,
            _ => 0,
        };
        var outcome = OperationOutcome.None;
        var watch = Stopwatch.StartNew();
        try
        {
            outcome = item switch
            {
                ExpandWork expand => await ExpandAsync(expand.Request, expand.Place).ConfigureAwait(false),
                UndoWork undo => await UndoAsync(undo.Request, undo.Place).ConfigureAwait(false),
                HotkeyWork hotkey => await InsertAsync(hotkey.PhraseId, hotkey.Place).ConfigureAwait(false),
                _ => OperationOutcome.None,
            };
        }
        catch (Exception ex)
        {
            LogOperationFailed(_logger, item.GetType().Name, ex);
        }
        finally
        {
            LogOperationDone(_logger, item.GetType().Name, watch.ElapsedMilliseconds);
            _expired.TryRemove(operationId, out _);
            _monitor?.Post(sequencer => sequencer.CompleteOperation(operationId, outcome));
        }
    }

    private async Task<OperationOutcome> ExpandAsync(ExpansionRequest request, Place place)
    {
        var match = request.Match;
        var phrase = _host.FindPhrase(match.Entry.PhraseId);
        if (phrase is null)
        {
            // The phrase was deleted a moment ago: deliver the held-back key as if nothing happened.
            if (StillThere(place))
            {
                _injector.Resend(request.CompletingKey, Physical());
            }

            return OperationOutcome.None;
        }

        await WaitForModifierReleaseAsync().ConfigureAwait(false);
        if (!StillThere(place))
        {
            // Never type into a place the user left. The trigger stays as typed, without its last key.
            return Abandon(place, phrase);
        }

        if (!_injector.SendBackspaces(match.CharactersToDelete))
        {
            _injector.Resend(request.CompletingKey, Physical());
            return OperationOutcome.None;
        }

        var settings = _host.Settings;
        var payload = BuildPayload(phrase.Content, settings.Rtf);
        if (phrase.Output == OutputTarget.Clipboard)
        {
            if (!await _clipboard.CopyAsync(payload).ConfigureAwait(false))
            {
                return PutTriggerBack(request, place, phrase, ExpansionNoticeKind.ClipboardBusy);
            }

            _host.PhraseInserted(phrase.Id, match.TypedText.Length);
            if (ResendDelimiter(request, place, phrase))
            {
                _host.Notify(new ExpansionNotice(ExpansionNoticeKind.CopiedToClipboard, phrase.Description));
            }

            return OperationOutcome.None;
        }

        var result = await PasteAsync(payload, place, settings.PasteTimeout).ConfigureAwait(false);
        switch (result)
        {
            case null:
                return PutTriggerBack(request, place, phrase, ExpansionNoticeKind.ClipboardBusy);
            case PasteOutcome.Waiting:
                return PutTriggerBack(request, place, phrase, ExpansionNoticeKind.TargetBusy);
            case PasteOutcome.Cancelled:
                // The trigger is already erased, but the user left (or the wait ran out) before the paste: say so.
                return Abandon(place, phrase);
        }

        // A delimiter (it can be Enter, which sends a chat message) follows only a confirmed or assumed paste,
        // and only while the user is still there.
        if (result is PasteOutcome.Consumed or PasteOutcome.Assumed && !ResendDelimiter(request, place, phrase))
        {
            _host.PhraseInserted(phrase.Id, match.TypedText.Length);
            return OperationOutcome.None;
        }

        _host.PhraseInserted(phrase.Id, match.TypedText.Length);
        if (result == PasteOutcome.TimedOut)
        {
            _host.Notify(new ExpansionNotice(ExpansionNoticeKind.PasteNotConfirmed, phrase.Description));
        }

        // Undo only after a confirmed paste of an immediate trigger: after a re-sent delimiter, Ctrl+Z
        // would undo the delimiter instead. Never in a terminal, where Ctrl+Z suspends the program.
        var undo = settings.UndoWithBackspace && result == PasteOutcome.Consumed && match.Delimiter is null && !_system.IsTerminal(place.Target.Window)
            ? new UndoTicket(phrase.Id, match.TypedText, DelimiterWasResent: false)
            : null;
        return new OperationOutcome(undo);
    }

    private async Task<OperationOutcome> InsertAsync(Guid phraseId, Place place)
    {
        var phrase = _host.FindPhrase(phraseId);
        if (phrase is null)
        {
            return OperationOutcome.None;
        }

        await WaitForModifierReleaseAsync().ConfigureAwait(false);
        if (!StillThere(place))
        {
            return Abandon(place, phrase);
        }

        var settings = _host.Settings;
        var payload = BuildPayload(phrase.Content, settings.Rtf);
        if (phrase.Output == OutputTarget.Clipboard)
        {
            var copied = await _clipboard.CopyAsync(payload).ConfigureAwait(false);
            _host.Notify(new ExpansionNotice(copied ? ExpansionNoticeKind.CopiedToClipboard : ExpansionNoticeKind.ClipboardBusy, phrase.Description));
            if (copied)
            {
                _host.PhraseInserted(phrase.Id, TypingSavings.HotkeyCharacters);
            }

            return OperationOutcome.None;
        }

        var result = await PasteAsync(payload, place, settings.PasteTimeout).ConfigureAwait(false);
        switch (result)
        {
            case null:
                _host.Notify(new ExpansionNotice(ExpansionNoticeKind.ClipboardBusy, phrase.Description));
                return OperationOutcome.None;
            case PasteOutcome.Waiting:
                _host.Notify(new ExpansionNotice(ExpansionNoticeKind.TargetBusy, phrase.Description));
                return OperationOutcome.None;
            case PasteOutcome.Cancelled:
                return Abandon(place, phrase);
        }

        _host.PhraseInserted(phrase.Id, TypingSavings.HotkeyCharacters);
        if (result == PasteOutcome.TimedOut)
        {
            _host.Notify(new ExpansionNotice(ExpansionNoticeKind.PasteNotConfirmed, phrase.Description));
        }

        return new OperationOutcome(settings.UndoWithBackspace && result == PasteOutcome.Consumed && !_system.IsTerminal(place.Target.Window)
            ? new UndoTicket(phrase.Id, string.Empty, DelimiterWasResent: false)
            : null);
    }

    private async Task<OperationOutcome> UndoAsync(UndoRequest request, Place place)
    {
        await WaitForModifierReleaseAsync().ConfigureAwait(false);
        if (!StillThere(place))
        {
            return OperationOutcome.None;
        }

        // Ctrl stays down a moment, so a busy app still sees Ctrl+Z, not Z, when it gets to the key.
        _injector.SendChord(InputInjector.VkControl, InputInjector.VkZ, keepModifierDown: true);
        try
        {
            await Task.Delay(s_undoModifierHold, _time, _stop.Token).ConfigureAwait(false);
        }
        finally
        {
            _injector.ReleaseKeys([InputInjector.VkControl]);
        }

        if (request.Ticket.TypedText.Length > 0)
        {
            _injector.TypeText(request.Ticket.TypedText);
        }

        return new OperationOutcome(SeedText: request.Ticket.TypedText);
    }

    /// <summary>
    /// Pastes through the clipboard and restores the user's clipboard afterwards. Returns <c>null</c>
    /// when the clipboard could not be opened (nothing was changed). After the target's read the operation
    /// ends at once (typing goes on), but the user's clipboard comes back a little later, in the background:
    /// some apps read the clipboard again while they paste (the first paste into an app is the slowest), and
    /// restoring too early would paste the user's own clipboard content. When the target does not read in
    /// time, the phrase stays on the clipboard until the late read, until the user leaves the target, or for
    /// 10 seconds at most. The user's clipboard comes back in every case, also when Velotext shuts down.
    /// </summary>
    private async Task<PasteOutcome?> PasteAsync(ClipboardPayload payload, Place place, TimeSpan timeout)
    {
        if (!_lateRestore.IsCompleted)
        {
            // An earlier paste was not confirmed and its phrase is still on the clipboard. A read now could
            // belong to either paste, so wait for that one to settle first.
            await Task.WhenAny(_lateRestore, Task.Delay(timeout, _time)).ConfigureAwait(false);
            if (!_lateRestore.IsCompleted)
            {
                return PasteOutcome.Waiting;
            }
        }

        var session = await _clipboard.BeginPasteAsync(payload, place.Target.ProcessId).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        var restoreLater = false;
        var ctrlDown = false;
        void ReleaseCtrl()
        {
            if (ctrlDown)
            {
                ctrlDown = false;
                _injector.ReleaseKeys([InputInjector.VkControl]);
            }
        }

        try
        {
            if (!StillThere(place))
            {
                return PasteOutcome.Cancelled;
            }

            var sentAt = _time.GetTimestamp();
            session.MarkPasteSent();
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                LogPasteSent(_logger, InputInjector.CurrentModifiers(), Physical());
            }

            // Ctrl stays down until the app has read the clipboard (or the wait ends): a busy app that checks
            // Ctrl only when it gets to the V would otherwise type "v".
            ctrlDown = true;
            if (!_injector.SendChord(InputInjector.VkControl, InputInjector.VkV, keepModifierDown: true))
            {
                return PasteOutcome.TimedOut;
            }

            // Wait for the target's read. When another program read first, the target's read may be hidden,
            // so a shorter fixed wait then decides.
            var consumed = session.Consumed;
            while (!consumed.IsCompleted)
            {
                var waited = _time.GetElapsedTime(sentAt);
                if (waited >= timeout || (session.OtherReaderSeen && waited >= s_assumedHold))
                {
                    break;
                }

                await Task.WhenAny(consumed, Task.Delay(s_pastePoll, _time)).ConfigureAwait(false);
            }

            ReleaseCtrl();
            LogPasteRead(_logger, consumed.IsCompleted, (long)_time.GetElapsedTime(sentAt).TotalMilliseconds, ProcessInfo.NameOf(place.Target.ProcessId));
            if (!consumed.IsCompleted && session.OtherReaderSeen)
            {
                restoreLater = true;
                _heldRestore = RestoreAfterHoldAsync(session, sentAt, RestoreHold(place.Target.ProcessId));
                return PasteOutcome.Assumed;
            }

            if (!consumed.IsCompleted)
            {
                restoreLater = true;
                _lateRestore = RestoreAfterLateReadAsync(session, sentAt, place.Target);
                return PasteOutcome.TimedOut;
            }

            if (session.ReplacedByOther)
            {
                return PasteOutcome.ReplacedByOther;
            }

            await WaitForReaderAsync(await consumed.ConfigureAwait(false), sentAt, place.Target.Window).ConfigureAwait(false);
            restoreLater = true;
            _heldRestore = RestoreAfterHoldAsync(session, sentAt, RestoreHold(place.Target.ProcessId));
            return PasteOutcome.Consumed;
        }
        finally
        {
            ReleaseCtrl();
            if (!restoreLater)
            {
                await RestoreQuietlyAsync(session).ConfigureAwait(false);
            }
        }
    }

    /// <summary>How long after the paste keystroke the user's clipboard comes back: longer for the first paste into a process.</summary>
    internal TimeSpan RestoreHold(uint processId) => _pastedProcesses.TryAdd(processId, true) ? s_firstPasteRestoreHold : s_restoreHold;

    private async Task RestoreAfterHoldAsync(PasteSession session, long sentAt, TimeSpan hold)
    {
        try
        {
            var remaining = hold - _time.GetElapsedTime(sentAt);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, _time, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Shutting down: restore at once.
        }
        finally
        {
            await RestoreQuietlyAsync(session).ConfigureAwait(false);
        }
    }

    private async Task RestoreAfterLateReadAsync(PasteSession session, long sentAt, ForegroundTarget target)
    {
        try
        {
            // The user gets their clipboard back as soon as they leave the target: they may want to paste it elsewhere.
            var stop = _stop.Token;
            var consumed = session.Consumed;
            var start = _time.GetTimestamp();
            while (!consumed.IsCompleted && _time.GetElapsedTime(start) < s_lateReadWait && _system.Foreground().Window == target.Window)
            {
                stop.ThrowIfCancellationRequested();
                await Task.WhenAny(consumed, Task.Delay(s_lateReadPoll, _time, stop)).ConfigureAwait(false);
            }

            if (consumed.IsCompleted && !session.ReplacedByOther)
            {
                await WaitForReaderAsync(await consumed.ConfigureAwait(false), sentAt, target.Window).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // Shutting down: restore at once.
        }
        finally
        {
            await RestoreQuietlyAsync(session).ConfigureAwait(false);
        }
    }

    private async Task RestoreQuietlyAsync(PasteSession session)
    {
        try
        {
            await _clipboard.RestoreAsync(session).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // The clipboard thread is gone; it restored any pending session when it stopped.
        }
    }

    /// <summary>
    /// Waits until the target has closed the clipboard and its window handles messages again (an app that
    /// pastes on its own thread has then finished; some read a second time while pasting), at least
    /// <see cref="s_minimumHold"/> after the paste keystroke, plus a short grace for a second read.
    /// Restoring too early would paste the user's own clipboard content into the target, so this errs on
    /// the side of waiting.
    /// </summary>
    private async Task WaitForReaderAsync(nint reader, long sentAt, nint targetWindow)
    {
        // A reader that opened the clipboard without a window cannot be watched; the grace covers it.
        var closeDeadline = _time.GetTimestamp();
        while (reader != 0 && _system.OpenClipboardWindow() == reader && _time.GetElapsedTime(closeDeadline) < s_readerCloseWait)
        {
            await Task.Delay(10, _stop.Token).ConfigureAwait(false);
        }

        await Task.Run(() => _system.WaitForWindowIdle(targetWindow, s_targetIdleWait), _stop.Token).ConfigureAwait(false);
        var remaining = s_minimumHold - _time.GetElapsedTime(sentAt);
        await Task.Delay(remaining > s_readerGrace ? remaining : s_readerGrace, _stop.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the user lets go of Shift, Ctrl, Alt and Win, so the paste shortcut is not turned into
    /// something else (Ctrl+Shift+V pastes plain text). After a second, held keys are released for them.
    /// </summary>
    private async Task WaitForModifierReleaseAsync()
    {
        var start = _time.GetTimestamp();
        while (true)
        {
            var held = s_modifierKeys.Where(_system.IsKeyDown).ToList();
            if (held.Count == 0)
            {
                return;
            }

            if (_time.GetElapsedTime(start) > s_modifierWait)
            {
                _injector.ReleaseKeys(held);
                return;
            }

            await Task.Delay(10, _stop.Token).ConfigureAwait(false);
        }
    }

    /// <summary>The focused window and the context generation right now, for an operation.</summary>
    private Place CurrentPlace(long operationId) => new(_system.Foreground(), _monitor?.ContextGeneration ?? 0, operationId);

    /// <summary>The modifier keys the user physically holds right now.</summary>
    private ModifierSides Physical() => _monitor?.HeldModifiers ?? ModifierSides.None;

    /// <summary>
    /// True when Velotext may still type for this operation: the user is in the same window, has not
    /// clicked or switched windows, and the sequencer has not given up on the operation.
    /// </summary>
    private bool StillThere(Place place) => Gone(place) is null;

    /// <summary>Why Velotext may no longer type for this operation, or <c>null</c> when it may.</summary>
    private string? Gone(Place place) =>
        place.Target.Window == 0 ? "no focused window"
        : _system.Foreground().Window != place.Target.Window ? "another window became active"
        : (_monitor?.ContextGeneration ?? 0) != place.Generation ? "a click or focus change"
        : _expired.ContainsKey(place.OperationId) ? "the operation took too long"
        : null;

    private OperationOutcome Abandon(Place place, Phrase phrase)
    {
        LogStopped(_logger, place.OperationId, Gone(place) ?? "unknown");
        Interlocked.Exchange(ref _abandonedOperation, place.OperationId);
        var kind = _expired.ContainsKey(place.OperationId) ? ExpansionNoticeKind.Expired : ExpansionNoticeKind.WindowChanged;
        _host.Notify(new ExpansionNotice(kind, phrase.Description));
        return OperationOutcome.None;
    }

    /// <summary>
    /// Re-sends the delimiter that completed a delimiter trigger, with the modifiers the user held, while
    /// the user is still there. Returns false (and tells the user) when the delimiter was skipped.
    /// </summary>
    private bool ResendDelimiter(ExpansionRequest request, Place place, Phrase phrase)
    {
        if (request.Match.Delimiter is null)
        {
            return true;
        }

        if (!StillThere(place))
        {
            // The delimiter can be Enter; typing it into the window the user moved to could send a message.
            LogStopped(_logger, place.OperationId, Gone(place) ?? "unknown");
            Interlocked.Exchange(ref _abandonedOperation, place.OperationId);
            _host.Notify(new ExpansionNotice(ExpansionNoticeKind.DelimiterSkipped, phrase.Description));
            return false;
        }

        _injector.Resend(request.CompletingKey, Physical());
        return true;
    }

    /// <summary>Retypes the erased trigger and delivers the held-back key, so the user's text is as typed.</summary>
    private OperationOutcome PutTriggerBack(ExpansionRequest request, Place place, Phrase phrase, ExpansionNoticeKind reason)
    {
        if (!StillThere(place))
        {
            return Abandon(place, phrase);
        }

        var match = request.Match;
        var erased = match.Delimiter is null ? match.TypedText[..^1] : match.TypedText;
        _injector.TypeText(erased);
        _injector.Resend(request.CompletingKey, Physical());
        _host.Notify(new ExpansionNotice(reason, phrase.Description, TriggerRestored: true));
        return OperationOutcome.None;
    }
    [LoggerMessage(Level = LogLevel.Debug, Message = "Paste keystroke sent; Windows had {Logical} down, the keyboard {Physical}.")]
    private static partial void LogPasteSent(ILogger logger, ModifierSides logical, ModifierSides physical);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Replayed {Count} held key events ({Refused} refused, {Modifiers} modifier keys); the keyboard holds {Physical}.")]
    private static partial void LogReplay(ILogger logger, int count, int refused, int modifiers, ModifierSides physical);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Operation {OperationId} stopped: {Reason}.")]
    private static partial void LogStopped(ILogger logger, long operationId, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Paste read by the target: {Read}, {Milliseconds} ms after the keystroke (target {Target}).")]
    private static partial void LogPasteRead(ILogger logger, bool read, long milliseconds, string target);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Operation {OperationId}: {Count} held key presses discarded after a click or window change.")]
    private static partial void LogDiscarded(ILogger logger, long operationId, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Operation} failed.")]
    private static partial void LogOperationFailed(ILogger logger, string operation, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Operation} finished in {Milliseconds} ms.")]
    private static partial void LogOperationDone(ILogger logger, string operation, long milliseconds);

    private abstract record Work;

    private sealed record ExpandWork(ExpansionRequest Request, Place Place) : Work;

    private sealed record UndoWork(UndoRequest Request, Place Place) : Work;

    private sealed record HotkeyWork(long OperationId, Guid PhraseId, Place Place) : Work;

    /// <summary>Where the user was when the operation started (focused window, context generation), and which operation it is.</summary>
    private readonly record struct Place(ForegroundTarget Target, long Generation, long OperationId);


}
