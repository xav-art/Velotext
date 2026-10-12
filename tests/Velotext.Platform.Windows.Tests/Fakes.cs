// Velotext: fakes for testing the expansion engine without a real keyboard or clipboard.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Collections.Concurrent;
using Velotext.Core.Input;
using Velotext.Core.Model;
using Velotext.Core.RichText;
using Velotext.Platform.Windows.Clipboard;
using Velotext.Platform.Windows.Expansion;
using Velotext.Platform.Windows.Input;
using Velotext.Platform.Windows.Interop;

namespace Velotext.Platform.Windows.Tests;

/// <summary>Records injected input as readable strings.</summary>
internal sealed class FakeInjector : IInputInjector
{
    public ConcurrentQueue<string> Calls { get; } = new();

    public bool Accept { get; set; } = true;

    public int RefuseReplay { get; set; }

    public bool SendBackspaces(int count)
    {
        Calls.Enqueue($"backspace x{count}");
        return Accept;
    }

    public bool SendChord(ushort modifier, ushort key, bool keepModifierDown = false)
    {
        Calls.Enqueue($"chord {modifier:X2}+{key:X2}");
        return Accept;
    }

    public void ReleaseKeys(IEnumerable<int> virtualKeys) => Calls.Enqueue("release " + string.Join(",", virtualKeys.Select(k => k.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))));

    public bool TypeText(string text)
    {
        Calls.Enqueue($"type '{text}'");
        return Accept;
    }

    public bool Resend(in KeyStroke key, ModifierSides physical)
    {
        Calls.Enqueue($"resend {(char)key.ScanCode}");
        return Accept;
    }

    public int Replay(IReadOnlyList<KeyStroke> keys, ModifierSides physical)
    {
        Calls.Enqueue($"replay {keys.Count}");
        return RefuseReplay;
    }
}

/// <summary>A clipboard whose paste sessions are consumed, ignored or replaced on command.</summary>
internal sealed class FakeClipboard : IClipboardService
{
    public enum Behaviour
    {
        Consume,
        Ignore,
        Replace,
        Busy,
        OtherReaderFirst,
    }

    public Behaviour Mode { get; set; } = Behaviour.Consume;

    public List<ClipboardPayload> Pasted { get; } = [];

    public List<ClipboardPayload> Copied { get; } = [];

    public int Restores { get; private set; }

    public bool CopySucceeds { get; set; } = true;

    public Task<PasteSession?> BeginPasteAsync(ClipboardPayload payload, uint targetProcessId)
    {
        if (Mode == Behaviour.Busy)
        {
            return Task.FromResult<PasteSession?>(null);
        }

        Pasted.Add(payload);
        var session = new PasteSession(payload, targetProcessId, new ClipboardSnapshot([]));
        if (Mode == Behaviour.Consume)
        {
            session.MarkConsumed(42);
        }
        else if (Mode == Behaviour.Replace)
        {
            session.MarkReplaced();
        }
        else if (Mode == Behaviour.OtherReaderFirst)
        {
            session.MarkOtherReader();
        }

        return Task.FromResult<PasteSession?>(session);
    }

    public Task RestoreAsync(PasteSession session)
    {
        Restores++;
        return Task.CompletedTask;
    }

    public Task<bool> CopyAsync(ClipboardPayload payload)
    {
        Copied.Add(payload);
        return Task.FromResult(CopySucceeds);
    }
}

/// <summary>Fixed system state.</summary>
internal sealed class FakeSystem : ISystemState
{
    public ForegroundTarget Target { get; set; } = new(100, 1234, 5678);

    // The engine reads key state on its worker while tests change it.
    public ConcurrentDictionary<int, bool> HeldKeys { get; } = new();

    public ForegroundTarget Foreground() => Target;

    public bool IsKeyDown(int virtualKey) => HeldKeys.ContainsKey(virtualKey);

    public nint OpenClipboardWindow() => 0;

    public HashSet<nint> Terminals { get; } = [];

    public bool IsTerminal(nint window) => Terminals.Contains(window);

    public int IdleWaits { get; private set; }

    public void WaitForWindowIdle(nint window, TimeSpan timeout) => IdleWaits++;
}

/// <summary>Runs sequencer calls synchronously and records completions.</summary>
internal sealed class FakeSequencerHost(InputSequencer sequencer) : ISequencerHost
{
    private readonly Lock _gate = new();
    private long _contextGeneration;

    public SemaphoreSlim Completed { get; } = new(0);

    public long ContextGeneration => Interlocked.Read(ref _contextGeneration);

    public ModifierSides HeldModifiers => ModifierSides.None;

    /// <summary>Simulates a click: the sequencer loses its context and running operations see the change.</summary>
    public void Click()
    {
        Interlocked.Increment(ref _contextGeneration);
        lock (_gate)
        {
            sequencer.OnContextLost();
        }
    }

    public void Post(Action<InputSequencer> action)
    {
        lock (_gate)
        {
            action(sequencer);
        }

        Completed.Release();
    }

    public Task<T> InvokeAsync<T>(Func<InputSequencer, T> func)
    {
        lock (_gate)
        {
            return Task.FromResult(func(sequencer));
        }
    }
}

/// <summary>Application host backed by a dictionary.</summary>
internal sealed class FakeHost : IExpansionHost
{
    public Dictionary<Guid, Phrase> Phrases { get; } = [];

    public ConcurrentQueue<ExpansionNotice> Notices { get; } = new();

    public ConcurrentQueue<Guid> Inserted { get; } = new();

    public ConcurrentQueue<int> TypedCharacters { get; } = new();

    public ExpansionSettings Settings { get; set; } = new(UndoWithBackspace: true, TimeSpan.FromMilliseconds(200), RtfOptions.Default);

    public Phrase? FindPhrase(Guid id) => Phrases.GetValueOrDefault(id);

    public void PhraseInserted(Guid id, int typedCharacters)
    {
        TypedCharacters.Enqueue(typedCharacters);
        Inserted.Enqueue(id);
    }

    public void Notify(ExpansionNotice notice) => Notices.Enqueue(notice);
}

/// <summary>Translator for tests: the scan code carries the character.</summary>
internal sealed class ScanCodeTranslator : IKeyTranslator
{
    public KeyMeaning Translate(in KeyStroke key) =>
        key.VirtualKey == 0x08 ? KeyMeaning.Backspace : KeyMeaning.ForText(((char)key.ScanCode).ToString());
}

/// <summary>Sink that forwards to the engine once it exists (the engine and sequencer reference each other).</summary>
internal sealed class ForwardingSink : IInputSequencerSink
{
    public IInputSequencerSink? Target { get; set; }

    public void ExpansionRequested(ExpansionRequest request) => Target!.ExpansionRequested(request);

    public void UndoRequested(UndoRequest request) => Target!.UndoRequested(request);

    public void ReplayRequested(IReadOnlyList<KeyStroke> keys) => Target!.ReplayRequested(keys);

    public void HeldKeysDiscarded(long operationId, int count) => Target!.HeldKeysDiscarded(operationId, count);

    public void OperationExpired(long operationId) => Target!.OperationExpired(operationId);
}
