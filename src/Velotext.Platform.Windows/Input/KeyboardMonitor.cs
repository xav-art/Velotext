// Velotext: installs the low-level keyboard and mouse hooks and runs the input sequencer on their thread.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Collections.Frozen;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Velotext.Core.Input;
using Velotext.Core.Matching;
using Velotext.Core.Model;
using Velotext.Platform.Windows.Interop;
using Velotext.Platform.Windows.Native;
using Velotext.Platform.Windows.Threading;

namespace Velotext.Platform.Windows.Input;

/// <summary>
/// Watches the keyboard with a low-level hook (no admin rights needed) on a dedicated high-priority
/// thread. Every key event goes through the <see cref="InputSequencer"/>, which decides in microseconds
/// whether it reaches the application, so the hook never risks the Windows hook timeout.
/// Mouse clicks and foreground window changes reset the typed context, because the caret may have moved.
/// Each key carries the modifiers the user physically held, so held keys can be replayed faithfully.
/// Windows silently removes a low-level hook that once answers too slowly; a watchdog compares the
/// system's last-input time with the hook's last call and reinstalls the hook when it went quiet.
/// The typed text never leaves this thread and is never stored or logged.
/// </summary>
public sealed partial class KeyboardMonitor : Expansion.ISequencerHost, IDisposable
{
    private const nuint TickTimerId = 1;
    private const uint TickIntervalMilliseconds = 200;
    private const int SilentHookMilliseconds = 1000;
    private const int ReinstallIntervalMilliseconds = 60_000;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private readonly ILogger<KeyboardMonitor> _logger;
    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;
    private readonly WinEventProc _foregroundProc;
    private readonly MessageLoopThread _thread;
    private readonly AutotextMatcher _matcher = new();
    private InputSequencer? _sequencer;
    private nint _keyboardHook;
    private nint _mouseHook;
    private nint _foregroundHook;
    private long _contextGeneration;
    private uint _lastHookCall;
    private int _lastWatchdogReinstall = Environment.TickCount - ReinstallIntervalMilliseconds;
    private bool _watchdogLogged;

    // Shift, Ctrl, Alt and Win as the user physically holds them, and the Alt or Win release mask.
    private readonly PhysicalKeyboardState _keyboard = new();
    private volatile FrozenSet<HotkeyGesture> _hotkeys = FrozenSet<HotkeyGesture>.Empty;

    /// <summary>Starts the hook thread and installs the hooks.</summary>
    /// <param name="sink">Receives expansion, undo and replay requests (on the hook thread).</param>
    /// <param name="time">Clock for sequencer timeouts.</param>
    /// <param name="options">Sequencer timeouts.</param>
    /// <param name="logger">Diagnostics logger. Never receives typed text.</param>
    public KeyboardMonitor(IInputSequencerSink sink, TimeProvider time, InputSequencerOptions options, ILogger<KeyboardMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(time);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _keyboardProc = OnKeyboard;
        _mouseProc = OnMouse;
        _foregroundProc = OnForegroundChanged;
        _thread = new MessageLoopThread(
            "Keyboard monitor",
            ThreadPriority.Highest,
            OnWindowMessage,
            ex => LogCallbackError(_logger, ex),
            loop =>
            {
                _sequencer = new InputSequencer(_matcher, new KeyboardTranslator(), sink, time, options);
                InstallHooks();
                NativeMethods.SetTimer(loop.WindowHandle, TickTimerId, TickIntervalMilliseconds, 0);
            });
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time);

    /// <inheritdoc />
    public long ContextGeneration => Interlocked.Read(ref _contextGeneration);

    /// <inheritdoc />
    public ModifierSides HeldModifiers => _keyboard.Sides;

    /// <summary>
    /// Sets the registered hotkeys. When one that uses Alt or Win is pressed, the release of Alt or Win is
    /// masked, so the app does not take it as a tap and open its menu. Safe from any thread.
    /// </summary>
    /// <param name="gestures">The registered hotkeys.</param>
    public void UpdateHotkeys(IEnumerable<HotkeyGesture> gestures) =>
        _hotkeys = (gestures ?? throw new ArgumentNullException(nameof(gestures))).ToFrozenSet();

    /// <summary>Replaces the autotext triggers. Safe from any thread; takes effect on the next key.</summary>
    /// <param name="index">The new trigger index.</param>
    public void UpdateTriggers(TriggerIndex index) => _matcher.Index = index ?? throw new ArgumentNullException(nameof(index));

    /// <summary>Runs an action against the sequencer on the hook thread.</summary>
    /// <param name="action">The action.</param>
    public void Post(Action<InputSequencer> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _thread.Post(() => action(_sequencer!));
    }

    /// <summary>Runs a function against the sequencer on the hook thread and returns its result.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="func">The function.</param>
    public Task<T> InvokeAsync<T>(Func<InputSequencer, T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        return _thread.InvokeAsync(() => func(_sequencer!));
    }

    /// <summary>
    /// Removes and re-installs the hooks. Windows silently drops a low-level hook that ever answers too
    /// slowly, so this runs after sleep and unlock to guarantee the hook is alive.
    /// </summary>
    public Task ReinstallAsync() => _thread.InvokeAsync(() =>
    {
        RemoveHooks();
        InstallHooks();
        LoseContext(_sequencer!);
    });

    /// <summary>Removes the hooks and stops the thread.</summary>
    public void Dispose()
    {
        try
        {
            _thread.InvokeAsync(() =>
            {
                NativeMethods.KillTimer(_thread.WindowHandle, TickTimerId);
                _sequencer?.ReleaseAll();
                RemoveHooks();
            }).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The thread is already gone; Windows removes hooks of exited threads.
        }

        _thread.Dispose();
    }

    private void InstallHooks()
    {
        var module = NativeMethods.GetModuleHandleW(null);
        _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WhKeyboardLl, Marshal.GetFunctionPointerForDelegate(_keyboardProc), module, 0);
        if (_keyboardHook == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not install the keyboard hook.");
        }

        _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WhMouseLl, Marshal.GetFunctionPointerForDelegate(_mouseProc), module, 0);
        _foregroundHook = NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemForeground,
            NativeMethods.EventSystemForeground,
            0,
            Marshal.GetFunctionPointerForDelegate(_foregroundProc),
            0,
            0,
            NativeMethods.WineventOutOfContext);
        _sequencer!.SuppressMatching = ForegroundTarget.Current().IsOwnProcess;
        ResyncModifiers();
        _lastHookCall = (uint)Environment.TickCount;
        LogHooksInstalled(_logger, _mouseHook != 0, _foregroundHook != 0);
    }

    private void RemoveHooks()
    {
        if (_keyboardHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }

        if (_mouseHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }

        if (_foregroundHook != 0)
        {
            NativeMethods.UnhookWinEvent(_foregroundHook);
            _foregroundHook = 0;
        }
    }

    private unsafe nint OnKeyboard(int code, nint wParam, nint lParam)
    {
        if (code == NativeMethods.HcAction && _sequencer is { } sequencer)
        {
            try
            {
                var data = (KeyboardHookData*)lParam;
                var origin = data->ExtraInfo switch
                {
                    InputMarkers.Own => KeyOrigin.Own,
                    InputMarkers.Replay => KeyOrigin.Replay,
                    _ => KeyOrigin.Physical,
                };
                _lastHookCall = (uint)Environment.TickCount;
                var up = (data->Flags & NativeMethods.LlkhfUp) != 0;
                var vk = (int)data->VirtualKey;
                if (origin == KeyOrigin.Physical)
                {
                    // With nothing held, Windows' own key state is exact; it corrects releases the hook never
                    // saw (the secure desktop after Ctrl+Alt+Del, an elevated window). While keys are held it
                    // lags behind, so the tracked state is kept then.
                    if (!sequencer.IsHolding)
                    {
                        ResyncModifiers();
                    }

                    var extended = (data->Flags & NativeMethods.LlkhfExtended) != 0;
                    _keyboard.Apply(vk, (int)data->ScanCode, extended, up);
                    var mask = _keyboard.Decide(vk, (int)data->ScanCode, extended, up, _hotkeys);
                    if (mask.Release is { } release)
                    {
                        // Sent now, so it reaches the app right after this event.
                        LogMaskedRelease(_logger, release.VirtualKey);
                        InputInjector.SendMaskedRelease(release.VirtualKey, release.ScanCode, release.Extended);
                    }

                    if (mask.Swallow)
                    {
                        return 1;
                    }
                }

                var attributes = (up ? KeyStrokeAttributes.KeyUp : KeyStrokeAttributes.None)
                    | ((data->Flags & NativeMethods.LlkhfExtended) != 0 ? KeyStrokeAttributes.Extended : KeyStrokeAttributes.None)
                    | _keyboard.Flags;
                var stroke = new KeyStroke(vk, (int)data->ScanCode, attributes, origin);
                var decision = sequencer.OnKey(stroke);
                if (stroke.IsModifierKey && _logger.IsEnabled(LogLevel.Debug))
                {
                    // Modifier keys only: never a key that types text.
                    LogModifierEvent(_logger, vk, up, origin, decision, _keyboard.Sides);
                }
                else if (origin == KeyOrigin.Replay && _logger.IsEnabled(LogLevel.Debug))
                {
                    // Shows that a replayed key went through the hook to the app, without saying which key.
                    LogReplayedKey(_logger, up, decision);
                }

                if (decision == KeyDecision.Swallow)
                {
                    return 1;
                }
            }
            catch (Exception ex)
            {
                // Never let an error block the user's keyboard.
                LogCallbackError(_logger, ex);
            }
        }

        return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
    }

    private void ResyncModifiers() =>
        _keyboard.Resync(
            InputInjector.CurrentModifiers(),
            ((NativeMethods.GetAsyncKeyState(VkLWin) | NativeMethods.GetAsyncKeyState(VkRWin)) & 0x8000) != 0);
    /// <summary>The caret may have moved: tell the sequencer and let running operations see the change.</summary>
    private void LoseContext(InputSequencer sequencer)
    {
        Interlocked.Increment(ref _contextGeneration);
        sequencer.OnContextLost();
    }

    private nint OnMouse(int code, nint wParam, nint lParam)
    {
        if (code == NativeMethods.HcAction && _sequencer is { } sequencer)
        {
            _lastHookCall = (uint)Environment.TickCount;
            var message = (uint)wParam;
            if (message is NativeMethods.WmLButtonDown or NativeMethods.WmRButtonDown or NativeMethods.WmMButtonDown or NativeMethods.WmXButtonDown)
            {
                try
                {
                    LogContextLost(_logger, "click");
                    LoseContext(sequencer);
                }
                catch (Exception ex)
                {
                    LogCallbackError(_logger, ex);
                }
            }
        }

        return NativeMethods.CallNextHookEx(0, code, wParam, lParam);
    }

    private void OnForegroundChanged(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time)
    {
        if (_sequencer is not { } sequencer)
        {
            return;
        }

        try
        {
            NativeMethods.GetWindowThreadProcessId(window, out var processId);
            sequencer.SuppressMatching = processId == (uint)Environment.ProcessId;
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                LogContextLost(_logger, "window change to " + ProcessInfo.NameOf(processId));
            }

            LoseContext(sequencer);
        }
        catch (Exception ex)
        {
            LogCallbackError(_logger, ex);
        }
    }

    private nint? OnWindowMessage(uint message, nint wParam, nint lParam)
    {
        if (message == NativeMethods.WmTimer && (nuint)wParam == TickTimerId)
        {
            _sequencer?.Tick();
            CheckHookAlive();
            return 0;
        }

        return null;
    }

    private void CheckHookAlive()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!NativeMethods.GetLastInputInfo(ref info))
        {
            return;
        }

        // Tick counts wrap around; unsigned subtraction keeps the comparison right.
        var silentFor = unchecked((int)(info.Time - _lastHookCall));
        if (silentFor < SilentHookMilliseconds
            || unchecked(Environment.TickCount - _lastWatchdogReinstall) < ReinstallIntervalMilliseconds
            || _sequencer is not { IsHolding: false }
            || ForegroundIsOutOfReach())
        {
            return;
        }

        // Input happened but the hooks were not called, and nothing explains it: Windows removed them.
        _lastWatchdogReinstall = Environment.TickCount;
        RemoveHooks();
        InstallHooks();
        if (!_watchdogLogged)
        {
            _watchdogLogged = true;
            LogWatchdogReinstall(_logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Keyboard hook installed (mouse hook: {MouseHook}, focus tracking: {ForegroundHook}).")]
    private static partial void LogHooksInstalled(ILogger logger, bool mouseHook, bool foregroundHook);

    /// <summary>
    /// True when the focused window's input never reaches the hook anyway: an app running as administrator,
    /// or a remote desktop client with its own keyboard hook. Reinstalling then would only change the order
    /// of hooks, so the watchdog leaves them alone.
    /// </summary>
    private static unsafe bool ForegroundIsOutOfReach()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == 0)
        {
            return true;
        }

        var buffer = stackalloc char[64];
        var length = NativeMethods.GetClassNameW(window, buffer, 64);
        var windowClass = length > 0 ? new string(buffer, 0, length) : string.Empty;
        if (windowClass is "TscShellContainerClass" or "Transparent Windows Client" or "CtxICADisp")
        {
            return true;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return ProcessInfo.IsElevatedOrHidden(processId);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The keyboard hook stopped receiving input; Velotext reinstalled it. Later reinstalls are not logged.")]
    private static partial void LogWatchdogReinstall(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Modifier key 0x{VirtualKey:X2} {Direction}: origin {Origin}, {Decision}; held now {Held}.")]
    private static partial void LogModifierEvent(ILogger logger, int virtualKey, bool direction, KeyOrigin origin, KeyDecision decision, ModifierSides held);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Replayed key event back through the hook (release: {Release}): {Decision}.")]
    private static partial void LogReplayedKey(ILogger logger, bool release, KeyDecision decision);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Release of 0x{VirtualKey:X2} after a hotkey sent behind a masking key.")]
    private static partial void LogMaskedRelease(ILogger logger, int virtualKey);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Typing context reset: {Cause}.")]
    private static partial void LogContextLost(ILogger logger, string cause);

    [LoggerMessage(Level = LogLevel.Error, Message = "Error in a keyboard monitor callback.")]
    private static partial void LogCallbackError(ILogger logger, Exception exception);
}
