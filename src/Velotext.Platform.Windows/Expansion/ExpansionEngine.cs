// Velotext: performs expansions, undos, hotkey insertions and key replays, one at a time, off the hook thread.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Velotext.Core.Input;
using Velotext.Core.Model;
using Velotext.Core.RichText;
using Velotext.Platform.Windows.Clipboard;
using Velotext.Platform.Windows.Input;

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

    /// <summary>The clipboard was held by another application; the trigger text was put back.</summary>
    ClipboardBusy = 2,
}

/// <summary>A notice for the user.</summary>
/// <param name="Kind">What happened.</param>
/// <param name="PhraseDescription">The phrase involved.</param>
public sealed record ExpansionNotice(ExpansionNoticeKind Kind, string PhraseDescription);

/// <summary>What the engine needs from the application.</summary>
public interface IExpansionHost
{
    /// <summary>Current expansion settings.</summary>
    ExpansionSettings Settings { get; }

    /// <summary>Finds a phrase in the current library snapshot.</summary>
    /// <param name="id">Phrase id.</param>
    Phrase? FindPhrase(Guid id);

    /// <summary>Called after a phrase was inserted or copied, for the "Last used" folder.</summary>
    /// <param name="id">Phrase id.</param>
    void PhraseInserted(Guid id);

    /// <summary>Shows a notice to the user. Called from a background thread.</summary>
    /// <param name="notice">The notice.</param>
    void Notify(ExpansionNotice notice);
}

/// <summary>
/// Executes the work the keyboard sequencer asks for, strictly one item at a time on a background
/// task, so expansions never overlap and the hook thread stays free. Every operation reports back
/// to the sequencer when it ends, which releases the keys typed meanwhile.
/// </summary>
public sealed partial class ExpansionEngine : IInputSequencerSink, IDisposable
{
    private static readonly TimeSpan s_modifierWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_minimumHold = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan s_readerGrace = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan s_readerCloseWait = TimeSpan.FromMilliseconds(500);
    private static readonly int[] s_modifierKeys = [0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C];

    private readonly IExpansionHost _host;
    private readonly IClipboardService _clipboard;
    private readonly IInputInjector _injector;
    private readonly ISystemState _system;
    private readonly TimeProvider _time;
    private readonly ILogger<ExpansionEngine> _logger;
    private readonly Channel<Work> _work = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private ISequencerHost? _monitor;

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
    }

    /// <summary>Connects the keyboard monitor that receives operation results. Call once, before typing starts.</summary>
    /// <param name="monitor">The keyboard monitor using this engine as its sink.</param>
    public void Attach(ISequencerHost monitor) => _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));

    /// <inheritdoc />
    public void ExpansionRequested(ExpansionRequest request) => _work.Writer.TryWrite(new ExpandWork(request));

    /// <inheritdoc />
    public void UndoRequested(UndoRequest request) => _work.Writer.TryWrite(new UndoWork(request));

    /// <inheritdoc />
    public void ReplayRequested(IReadOnlyList<KeyStroke> keys) => _work.Writer.TryWrite(new ReplayWork(keys));

    /// <summary>Inserts a phrase because its hotkey was pressed. Ignored while paused or inside Velotext.</summary>
    /// <param name="phraseId">The phrase to insert.</param>
    public async Task InsertFromHotkeyAsync(Guid phraseId)
    {
        if (_monitor is null || _system.Foreground().IsOwnProcess)
        {
            return;
        }

        var operationId = await _monitor.InvokeAsync(sequencer => sequencer.Enabled ? sequencer.BeginOperation() : 0).ConfigureAwait(false);
        if (operationId != 0)
        {
            _work.Writer.TryWrite(new HotkeyWork(operationId, phraseId));
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
        _work.Writer.TryComplete();
        _stop.Cancel();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
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

    private async Task ExecuteAsync(Work item)
    {
        if (item is ReplayWork replay)
        {
            var refused = _injector.Replay(replay.Keys);
            if (refused > 0)
            {
                _monitor?.Post(sequencer => sequencer.ReplayInjectionFailed(refused));
            }

            return;
        }

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
                ExpandWork expand => await ExpandAsync(expand.Request).ConfigureAwait(false),
                UndoWork undo => await UndoAsync(undo.Request).ConfigureAwait(false),
                HotkeyWork hotkey => await InsertAsync(hotkey.PhraseId).ConfigureAwait(false),
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
            _monitor?.Post(sequencer => sequencer.CompleteOperation(operationId, outcome));
        }
    }

    private async Task<OperationOutcome> ExpandAsync(ExpansionRequest request)
    {
        var match = request.Match;
        var phrase = _host.FindPhrase(match.Entry.PhraseId);
        if (phrase is null)
        {
            // The phrase was deleted a moment ago: deliver the held-back key as if nothing happened.
            _injector.Resend(request.CompletingKey);
            return OperationOutcome.None;
        }

        var target = _system.Foreground();
        await WaitForModifierReleaseAsync().ConfigureAwait(false);
        if (!_injector.SendBackspaces(match.CharactersToDelete))
        {
            _injector.Resend(request.CompletingKey);
            return OperationOutcome.None;
        }

        var settings = _host.Settings;
        var payload = BuildPayload(phrase.Content, settings.Rtf);
        if (phrase.Output == OutputTarget.Clipboard)
        {
            var copied = await _clipboard.CopyAsync(payload).ConfigureAwait(false);
            if (!copied)
            {
                PutTriggerBack(request);
                _host.Notify(new ExpansionNotice(ExpansionNoticeKind.ClipboardBusy, phrase.Description));
                return OperationOutcome.None;
            }

            ResendDelimiter(request);
            _host.PhraseInserted(phrase.Id);
            _host.Notify(new ExpansionNotice(ExpansionNoticeKind.CopiedToClipboard, phrase.Description));
            return OperationOutcome.None;
        }

        var result = await PasteAsync(payload, target.ProcessId, settings.PasteTimeout).ConfigureAwait(false);
        if (result is null)
        {
            PutTriggerBack(request);
            _host.Notify(new ExpansionNotice(ExpansionNoticeKind.ClipboardBusy, phrase.Description));
            return OperationOutcome.None;
        }

        ResendDelimiter(request);
        _host.PhraseInserted(phrase.Id);
        if (result == PasteOutcome.TimedOut)
        {
            _host.Notify(new ExpansionNotice(ExpansionNoticeKind.PasteNotConfirmed, phrase.Description));
        }

        // Undo only when the paste is known to have happened; otherwise Ctrl+Z would undo something else.
        var undo = settings.UndoWithBackspace && result == PasteOutcome.Consumed
            ? new UndoTicket(phrase.Id, match.TypedText, DelimiterWasResent: match.Delimiter is not null)
            : null;
        return new OperationOutcome(undo);
    }

    private async Task<OperationOutcome> InsertAsync(Guid phraseId)
    {
        var phrase = _host.FindPhrase(phraseId);
        if (phrase is null)
        {
            return OperationOutcome.None;
        }

        var target = _system.Foreground();
        await WaitForModifierReleaseAsync().ConfigureAwait(false);
        var settings = _host.Settings;
        var payload = BuildPayload(phrase.Content, settings.Rtf);
        if (phrase.Output == OutputTarget.Clipboard)
        {
            var copied = await _clipboard.CopyAsync(payload).ConfigureAwait(false);
            _host.Notify(new ExpansionNotice(copied ? ExpansionNoticeKind.CopiedToClipboard : ExpansionNoticeKind.ClipboardBusy, phrase.Description));
            if (copied)
            {
                _host.PhraseInserted(phrase.Id);
            }

            return OperationOutcome.None;
        }

        var result = await PasteAsync(payload, target.ProcessId, settings.PasteTimeout).ConfigureAwait(false);
        if (result is null)
        {
            _host.Notify(new ExpansionNotice(ExpansionNoticeKind.ClipboardBusy, phrase.Description));
            return OperationOutcome.None;
        }

        _host.PhraseInserted(phrase.Id);
        if (result == PasteOutcome.TimedOut)
        {
            _host.Notify(new ExpansionNotice(ExpansionNoticeKind.PasteNotConfirmed, phrase.Description));
        }

        return new OperationOutcome(settings.UndoWithBackspace && result == PasteOutcome.Consumed
            ? new UndoTicket(phrase.Id, string.Empty, DelimiterWasResent: false)
            : null);
    }

    private async Task<OperationOutcome> UndoAsync(UndoRequest request)
    {
        await WaitForModifierReleaseAsync().ConfigureAwait(false);
        _injector.SendChord(InputInjector.VkControl, InputInjector.VkZ);
        if (request.Ticket.TypedText.Length > 0)
        {
            _injector.TypeText(request.Ticket.TypedText);
        }

        return new OperationOutcome(SeedText: request.Ticket.TypedText);
    }

    /// <summary>
    /// Pastes through the clipboard and restores the user's clipboard afterwards. Returns <c>null</c>
    /// when the clipboard could not be opened (nothing was changed).
    /// </summary>
    private async Task<PasteOutcome?> PasteAsync(ClipboardPayload payload, uint targetProcessId, TimeSpan timeout)
    {
        var session = await _clipboard.BeginPasteAsync(payload, targetProcessId).ConfigureAwait(false);
        if (session is null)
        {
            return null;
        }

        var sentAt = _time.GetTimestamp();
        PasteOutcome outcome;
        if (!_injector.SendChord(InputInjector.VkControl, InputInjector.VkV))
        {
            outcome = PasteOutcome.TimedOut;
        }
        else
        {
            var consumed = session.Consumed;
            await Task.WhenAny(consumed, Task.Delay(timeout, _time)).ConfigureAwait(false);
            if (!consumed.IsCompleted)
            {
                outcome = PasteOutcome.TimedOut;
            }
            else if (session.ReplacedByOther)
            {
                outcome = PasteOutcome.ReplacedByOther;
            }
            else
            {
                outcome = PasteOutcome.Consumed;
                await WaitForReaderAsync(await consumed.ConfigureAwait(false), sentAt).ConfigureAwait(false);
            }
        }

        await _clipboard.RestoreAsync(session).ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Waits until the target has closed the clipboard, at least <see cref="s_minimumHold"/> after the
    /// paste keystroke, plus a short grace for a second read. Restoring too early would paste the
    /// user's own clipboard content into the target, so this errs on the side of waiting.
    /// </summary>
    private async Task WaitForReaderAsync(nint reader, long sentAt)
    {
        var closeDeadline = _time.GetTimestamp();
        while (_system.OpenClipboardWindow() == reader && _time.GetElapsedTime(closeDeadline) < s_readerCloseWait)
        {
            await Task.Delay(10, _stop.Token).ConfigureAwait(false);
        }

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

    private void ResendDelimiter(ExpansionRequest request)
    {
        if (request.Match.Delimiter is not null)
        {
            _injector.Resend(request.CompletingKey);
        }
    }

    /// <summary>Retypes the erased trigger and delivers the held-back key, so the user's text is as typed.</summary>
    private void PutTriggerBack(ExpansionRequest request)
    {
        var match = request.Match;
        var erased = match.Delimiter is null ? match.TypedText[..^1] : match.TypedText;
        _injector.TypeText(erased);
        _injector.Resend(request.CompletingKey);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "{Operation} failed.")]
    private static partial void LogOperationFailed(ILogger logger, string operation, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Operation} finished in {Milliseconds} ms.")]
    private static partial void LogOperationDone(ILogger logger, string operation, long milliseconds);

    private abstract record Work;

    private sealed record ExpandWork(ExpansionRequest Request) : Work;

    private sealed record UndoWork(UndoRequest Request) : Work;

    private sealed record HotkeyWork(long OperationId, Guid PhraseId) : Work;

    private sealed record ReplayWork(IReadOnlyList<KeyStroke> Keys) : Work;
}
