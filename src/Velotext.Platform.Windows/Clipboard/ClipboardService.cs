// Velotext: clipboard access for pasting phrases and restoring the user's clipboard afterwards.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Velotext.Platform.Windows.Interop;
using Velotext.Platform.Windows.Native;
using Velotext.Platform.Windows.Threading;

namespace Velotext.Platform.Windows.Clipboard;

/// <summary>The formats of one phrase on the clipboard.</summary>
/// <param name="Text">Plain text (always present).</param>
/// <param name="Html">Windows "HTML Format" payload, or <c>null</c> for plain phrases.</param>
/// <param name="Rtf">RTF, or <c>null</c> for plain phrases.</param>
public sealed record ClipboardPayload(string Text, string? Html, string? Rtf);

/// <summary>How a paste ended.</summary>
public enum PasteOutcome
{
    /// <summary>The target application read the phrase.</summary>
    Consumed = 0,

    /// <summary>No read by the target was seen before the timeout.</summary>
    TimedOut = 1,

    /// <summary>Someone else put new content on the clipboard meanwhile; it is left alone.</summary>
    ReplacedByOther = 2,

    /// <summary>The user clicked or changed windows before the paste keystroke, so it was not sent.</summary>
    Cancelled = 3,

    /// <summary>
    /// Another program read the phrase before the target, so the target's own read could not be seen; after a
    /// fixed wait the paste is taken as done.
    /// </summary>
    Assumed = 5,

    /// <summary>An earlier paste is not confirmed yet, so this one was not started (a late read could belong to either).</summary>
    Waiting = 4,
}

/// <summary>
/// Clipboard access on a dedicated thread. To paste, Velotext saves the current clipboard, offers the
/// phrase with delayed rendering (so it can see exactly when the target application reads it), and
/// restores the saved clipboard once the target has read it. The temporary content carries the
/// Windows flags that keep it out of clipboard history, cloud clipboard and clipboard managers.
/// While Velotext waits to open the clipboard it keeps answering render requests, so a reader that
/// holds the clipboard open is never blocked. A restore that cannot open the clipboard is retried, and
/// stopping the service restores any pending paste first, so the user's clipboard is not lost.
/// </summary>
public sealed partial class ClipboardService : Expansion.IClipboardService, IDisposable
{
    private const int OpenAttempts = 25;
    private const int RestoreRetries = 20;
    private const nuint RestoreTimerId = 1;
    private const uint RestoreRetryMilliseconds = 250;
    private const int MaxFormatBytes = 32 * 1024 * 1024;
    private const int MaxSnapshotBytes = 64 * 1024 * 1024;

    // GDI handle formats cannot be copied as memory; Windows re-creates bitmaps from CF_DIB anyway.
    private static readonly HashSet<uint> s_handleFormats = [2, 3, 9, 14, 0x80, 0x82, 0x83, 0x8E];

    // OLE formats reference objects inside the original application and are useless as raw bytes.
    private static readonly HashSet<string> s_oleFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "DataObject", "Ole Private Data", "OwnerLink", "ObjectLink", "Link Source", "Embed Source",
        "Embedded Object", "Link Source Descriptor", "Object Descriptor",
    };

    private readonly ILogger<ClipboardService> _logger;
    private readonly MessageLoopThread _thread;
    private readonly uint _htmlFormat;
    private readonly uint _rtfFormat;
    private readonly uint _excludeFormat;
    private readonly uint _historyFormat;
    private readonly uint _cloudFormat;
    private PasteSession? _session;
    private bool _emptyingOurselves;
    private int _restoreRetriesLeft;

    /// <summary>Starts the clipboard thread.</summary>
    /// <param name="logger">Diagnostics logger. Never receives clipboard content.</param>
    public ClipboardService(ILogger<ClipboardService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _htmlFormat = NativeMethods.RegisterClipboardFormatW("HTML Format");
        _rtfFormat = NativeMethods.RegisterClipboardFormatW("Rich Text Format");
        _excludeFormat = NativeMethods.RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
        _historyFormat = NativeMethods.RegisterClipboardFormatW("CanIncludeInClipboardHistory");
        _cloudFormat = NativeMethods.RegisterClipboardFormatW("CanUploadToCloudClipboard");
        _thread = new MessageLoopThread("Clipboard", ThreadPriority.AboveNormal, OnWindowMessage, ex => LogError(_logger, ex));
    }

    /// <summary>Saves the clipboard and offers <paramref name="payload"/> for pasting into <paramref name="targetProcessId"/>.</summary>
    /// <param name="payload">The phrase formats.</param>
    /// <param name="targetProcessId">Process that should read it (the foreground application).</param>
    /// <returns>The paste session, or <c>null</c> when the clipboard could not be opened.</returns>
    public Task<PasteSession?> BeginPasteAsync(ClipboardPayload payload, uint targetProcessId)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return _thread.InvokeAsync(() =>
        {
            ClipboardSnapshot? snapshot = null;
            for (var attempt = 0; attempt < 2 && snapshot is null; attempt++)
            {
                // If an earlier paste still waits to restore, the clipboard holds that phrase, not the user's
                // content. Reuse the earlier snapshot, which is the user's real clipboard.
                var pending = _session is { ReplacedByOther: false } current ? current : null;
                uint sequence = 0;
                var saved = pending?.Snapshot ?? Capture(out sequence);
                if (saved is null || !TryOpen())
                {
                    return null;
                }

                // Opening may have waited; check that nobody changed the clipboard in between. If someone
                // did, close and save the new content instead of overwriting it.
                var unchanged = pending is null
                    ? NativeMethods.GetClipboardSequenceNumber() == sequence
                    : !pending.ReplacedByOther && NativeMethods.GetClipboardOwner() == _thread.WindowHandle;
                if (unchanged)
                {
                    snapshot = saved;
                }
                else
                {
                    NativeMethods.CloseClipboard();
                    if (pending is not null)
                    {
                        pending.MarkReplaced();
                    }
                }
            }

            if (snapshot is null)
            {
                return null;
            }

            var session = new PasteSession(payload, targetProcessId, snapshot);
            try
            {
                EmptyAsOwner();

                // The new session takes over the saved clipboard, so a pending restore retry stops.
                NativeMethods.KillTimer(_thread.WindowHandle, RestoreTimerId);
                _session = session;

                // Content formats are rendered on request, which tells us when the target reads them.
                if (payload.Html is not null)
                {
                    NativeMethods.SetClipboardData(_htmlFormat, 0);
                }

                if (payload.Rtf is not null)
                {
                    NativeMethods.SetClipboardData(_rtfFormat, 0);
                }

                NativeMethods.SetClipboardData(NativeMethods.CfUnicodeText, 0);
                SetPrivacyFlags();
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }

            return session;
        });
    }

    /// <summary>Puts the user's saved clipboard back, unless someone replaced the content meanwhile.</summary>
    /// <param name="session">The finished paste session.</param>
    public Task RestoreAsync(PasteSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _thread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_session, session) || TryRestore(session))
            {
                return;
            }

            // Another application holds the clipboard. Keep the session, so its formats can still be
            // rendered, and try again shortly.
            _restoreRetriesLeft = RestoreRetries;
            NativeMethods.SetTimer(_thread.WindowHandle, RestoreTimerId, RestoreRetryMilliseconds, 0);
        });
    }

    /// <summary>Copies a phrase to the clipboard for the user to paste later (the Clipboard output target).</summary>
    /// <param name="payload">The phrase formats.</param>
    /// <returns>True when the clipboard was updated.</returns>
    public Task<bool> CopyAsync(ClipboardPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return _thread.InvokeAsync(() =>
        {
            if (!TryOpen())
            {
                return false;
            }

            try
            {
                // The user asked for the phrase on the clipboard; a pending restore is no longer wanted.
                NativeMethods.KillTimer(_thread.WindowHandle, RestoreTimerId);
                if (_session is { } pending)
                {
                    _session = null;
                    pending.Snapshot.Clear();
                }

                EmptyAsOwner();
                if (payload.Html is not null)
                {
                    SetData(_htmlFormat, Utf8WithNull(payload.Html));
                }

                if (payload.Rtf is not null)
                {
                    SetData(_rtfFormat, AsciiWithNull(payload.Rtf));
                }

                SetData(NativeMethods.CfUnicodeText, Utf16WithNull(payload.Text));
                return true;
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
        });
    }


    /// <summary>Restores a pending paste, then stops the clipboard thread.</summary>
    public void Dispose()
    {
        try
        {
            _thread.InvokeAsync(() =>
            {
                if (_session is { } session && !TryRestore(session))
                {
                    LogRestoreFailed(_logger);
                }
            }).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The thread is already gone.
        }

        _thread.Dispose();
    }

    /// <summary>Puts the saved clipboard back and ends the session. Returns false when the clipboard could not be opened.</summary>
    private bool TryRestore(PasteSession session)
    {
        if (session.ReplacedByOther)
        {
            EndSession(session);
            return true;
        }

        if (!TryOpen())
        {
            return false;
        }

        try
        {
            // Opening may have waited and answered messages. If another app put new content on the
            // clipboard meanwhile, it stays: restoring would overwrite the user's newer copy.
            if (session.ReplacedByOther || NativeMethods.GetClipboardOwner() != _thread.WindowHandle)
            {
                session.MarkReplaced();
                EndSession(session);
                LogNotRestored(_logger);
                return true;
            }

            // The session stays current until the clipboard is open, so render requests are answered meanwhile.
            EndSession(session);
            EmptyAsOwner();
            foreach (var (format, data) in session.Snapshot.Formats)
            {
                SetData(format, data);
            }

            // The restored content is already in clipboard history; do not add it twice. An empty
            // clipboard stays truly empty.
            if (session.Snapshot.Formats.Count > 0)
            {
                SetData(_excludeFormat, BitConverter.GetBytes(0));
            }

            // Windows now holds its own copy; do not keep the user's clipboard content in Velotext's memory.
            LogRestored(_logger, session.Snapshot.Formats.Count);
            session.Snapshot.Clear();
            return true;
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private void EndSession(PasteSession session)
    {
        if (ReferenceEquals(_session, session))
        {
            _session = null;
        }

        NativeMethods.KillTimer(_thread.WindowHandle, RestoreTimerId);
        if (session.ReplacedByOther)
        {
            session.Snapshot.Clear();
        }
    }

    private void RetryRestore()
    {
        if (_session is not { } session)
        {
            NativeMethods.KillTimer(_thread.WindowHandle, RestoreTimerId);
            return;
        }

        if (TryRestore(session))
        {
            return;
        }

        if (--_restoreRetriesLeft <= 0)
        {
            // Give up: leave the phrase on the clipboard rather than an empty clipboard.
            LogRestoreFailed(_logger);
            RenderAllNow(session);
            EndSession(session);
            session.Snapshot.Clear();
        }
    }

    private nint? OnWindowMessage(uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case NativeMethods.WmTimer when (nuint)wParam == RestoreTimerId:
                RetryRestore();
                return 0;
            case NativeMethods.WmRenderFormat:
                RenderFormat((uint)wParam);
                return 0;
            case NativeMethods.WmRenderAllFormats:
                RenderAll();
                return 0;
            case NativeMethods.WmDestroyClipboard:
                if (!_emptyingOurselves && _session is { } session)
                {
                    session.MarkReplaced();
                }

                return 0;
            default:
                return null;
        }
    }

    private void RenderFormat(uint format)
    {
        if (_session is not { } session)
        {
            return;
        }

        // A read by the paste target, or a helper process of it (browsers read from a child process),
        // confirms the paste. Some clipboard monitors ignore the privacy flags and read every change; they
        // are served too (refusing them makes them retry and keep the clipboard busy while the target tries
        // to paste), but their read only tells the engine that the target's own read may be hidden: once a
        // format is rendered, later reads of it are invisible. A reader without a window cannot be told
        // apart and counts as the target. A read before the paste keystroke cannot be the paste.
        var requester = NativeMethods.GetOpenClipboardWindow();
        uint requesterProcess = 0;
        if (requester != 0)
        {
            NativeMethods.GetWindowThreadProcessId(requester, out requesterProcess);
        }

        var isTarget = requester == 0 || ProcessTree.IsSameOrDescendant(requesterProcess, session.TargetProcessId);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            LogRender(_logger, format == _htmlFormat ? "HTML" : format == _rtfFormat ? "RTF" : format == NativeMethods.CfUnicodeText ? "text" : "other", ProcessInfo.NameOfWindowOwner(requester), session.PasteSent, isTarget);
        }

        if (session.PasteSent)
        {
            if (isTarget)
            {
                session.MarkConsumed(requester);
            }
            else
            {
                session.MarkOtherReader();
            }
        }

        var data = Render(session.Payload, format);
        if (data is not null)
        {
            SetData(format, data);
        }
    }

    private void RenderAll()
    {
        if (_session is { } session)
        {
            RenderAllNow(session);
        }
    }

    /// <summary>Replaces the delayed formats with real data, so the clipboard is not left with formats that render nothing.</summary>
    private void RenderAllNow(PasteSession session)
    {
        if (!TryOpen())
        {
            return;
        }

        try
        {
            if (NativeMethods.GetClipboardOwner() != _thread.WindowHandle)
            {
                return;
            }

            foreach (var format in new[] { _htmlFormat, _rtfFormat, NativeMethods.CfUnicodeText })
            {
                if (Render(session.Payload, format) is { } data)
                {
                    SetData(format, data);
                }
            }
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private byte[]? Render(ClipboardPayload payload, uint format)
    {
        if (format == _htmlFormat && payload.Html is not null)
        {
            return Utf8WithNull(payload.Html);
        }

        if (format == _rtfFormat && payload.Rtf is not null)
        {
            return AsciiWithNull(payload.Rtf);
        }

        return format == NativeMethods.CfUnicodeText ? Utf16WithNull(payload.Text) : null;
    }

    private void SetPrivacyFlags()
    {
        var zero = BitConverter.GetBytes(0);
        SetData(_excludeFormat, zero);
        SetData(_historyFormat, zero);
        SetData(_cloudFormat, zero);
    }

    private void EmptyAsOwner()
    {
        _emptyingOurselves = true;
        try
        {
            NativeMethods.EmptyClipboard();
        }
        finally
        {
            _emptyingOurselves = false;
        }
    }

    private unsafe bool TryOpen()
    {
        // Another application may hold the clipboard for a moment; retry briefly. While waiting, answer
        // messages sent by other threads: the holder may be waiting for this thread to render a format.
        for (var attempt = 0; attempt < OpenAttempts; attempt++)
        {
            if (NativeMethods.OpenClipboard(_thread.WindowHandle))
            {
                return true;
            }

            NativeMethods.MsgWaitForMultipleObjectsEx(0, null, 10, NativeMethods.QsSendMessage, 0);
            NativeMethods.PeekMessageW(out _, 0, 0, 0, NativeMethods.PmNoRemoveSentOnly);
        }

        LogClipboardBusy(_logger);
        return false;
    }

    private unsafe ClipboardSnapshot? Capture(out uint sequence)
    {
        sequence = 0;
        if (!TryOpen())
        {
            return null;
        }

        try
        {
            sequence = NativeMethods.GetClipboardSequenceNumber();
            var formats = new List<(uint, byte[])>();
            long total = 0;
            uint format = 0;
            while ((format = NativeMethods.EnumClipboardFormats(format)) != 0)
            {
                if (s_handleFormats.Contains(format) || (format >= 0xC000 && s_oleFormats.Contains(FormatName(format))))
                {
                    continue;
                }

                var handle = NativeMethods.GetClipboardData(format);
                if (handle == 0)
                {
                    continue;
                }

                var size = (long)NativeMethods.GlobalSize(handle);
                if (size <= 0 || size > MaxFormatBytes || total + size > MaxSnapshotBytes)
                {
                    continue;
                }

                var pointer = NativeMethods.GlobalLock(handle);
                if (pointer == 0)
                {
                    continue;
                }

                try
                {
                    var bytes = new byte[size];
                    Marshal.Copy(pointer, bytes, 0, (int)size);
                    formats.Add((format, bytes));
                    total += size;
                }
                finally
                {
                    NativeMethods.GlobalUnlock(handle);
                }
            }

            return new ClipboardSnapshot(formats);
        }
        finally
        {
            NativeMethods.CloseClipboard();
        }
    }

    private static unsafe string FormatName(uint format)
    {
        var buffer = stackalloc char[256];
        var length = NativeMethods.GetClipboardFormatNameW(format, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static void SetData(uint format, byte[] data)
    {
        var handle = NativeMethods.GlobalAlloc(NativeMethods.GmemMoveable, (nuint)Math.Max(1, data.Length));
        if (handle == 0)
        {
            return;
        }

        var pointer = NativeMethods.GlobalLock(handle);
        if (pointer == 0)
        {
            NativeMethods.GlobalFree(handle);
            return;
        }

        Marshal.Copy(data, 0, pointer, data.Length);
        NativeMethods.GlobalUnlock(handle);
        if (NativeMethods.SetClipboardData(format, handle) == 0)
        {
            // Ownership passes to Windows only on success.
            NativeMethods.GlobalFree(handle);
        }
    }

    private static byte[] Utf8WithNull(string text) => [.. Encoding.UTF8.GetBytes(text), 0];

    private static byte[] AsciiWithNull(string text) => [.. Encoding.ASCII.GetBytes(text), 0];

    private static byte[] Utf16WithNull(string text) => [.. Encoding.Unicode.GetBytes(text), 0, 0];

    [LoggerMessage(Level = LogLevel.Warning, Message = "The clipboard is held by another application.")]
    private static partial void LogClipboardBusy(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Clipboard read: {Format} by {Reader} (after the paste keystroke: {AfterPaste}, target: {IsTarget}).")]
    private static partial void LogRender(ILogger logger, string format, string reader, bool afterPaste, bool isTarget);

    [LoggerMessage(Level = LogLevel.Debug, Message = "User clipboard restored ({Formats} formats).")]
    private static partial void LogRestored(ILogger logger, int formats);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Another app changed the clipboard meanwhile; it was left as it is.")]
    private static partial void LogNotRestored(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Velotext could not put the user's clipboard back; the phrase stays on the clipboard.")]
    private static partial void LogRestoreFailed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Clipboard thread error.")]
    private static partial void LogError(ILogger logger, Exception exception);
}

/// <summary>A saved copy of the clipboard's memory-based formats, in their original order.</summary>
/// <param name="Formats">Format ids and their bytes.</param>
public sealed record ClipboardSnapshot(IReadOnlyList<(uint Format, byte[] Data)> Formats)
{
    /// <summary>Overwrites the saved bytes with zeros once they are no longer needed.</summary>
    public void Clear()
    {
        foreach (var (_, data) in Formats)
        {
            Array.Clear(data);
        }
    }
}

/// <summary>One paste in progress: what is offered, to whom, and what was saved.</summary>
public sealed class PasteSession
{
    private readonly TaskCompletionSource<nint> _consumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _replaced;
    private volatile bool _pasteSent;
    private volatile bool _otherReader;

    internal PasteSession(ClipboardPayload payload, uint targetProcessId, ClipboardSnapshot snapshot)
    {
        Payload = payload;
        TargetProcessId = targetProcessId;
        Snapshot = snapshot;
    }

    /// <summary>The offered phrase.</summary>
    public ClipboardPayload Payload { get; }

    /// <summary>The process expected to read it.</summary>
    public uint TargetProcessId { get; }

    /// <summary>The saved user clipboard.</summary>
    public ClipboardSnapshot Snapshot { get; }

    /// <summary>True when another application replaced the clipboard; it must not be restored then.</summary>
    public bool ReplacedByOther => _replaced;

    /// <summary>True once the paste keystroke was sent; only reads after it confirm the paste.</summary>
    public bool PasteSent => _pasteSent;

    /// <summary>Marks the paste keystroke as sent. Call right before sending it.</summary>
    public void MarkPasteSent() => _pasteSent = true;

    /// <summary>True when a program other than the target read the phrase after the paste keystroke.</summary>
    public bool OtherReaderSeen => _otherReader;

    internal void MarkOtherReader() => _otherReader = true;

    /// <summary>Completes with the reading window when the target reads the phrase.</summary>
    public Task<nint> Consumed => _consumed.Task;

    internal void MarkConsumed(nint window) => _consumed.TrySetResult(window);

    internal void MarkReplaced()
    {
        _replaced = true;
        _consumed.TrySetResult(0);
    }
}
