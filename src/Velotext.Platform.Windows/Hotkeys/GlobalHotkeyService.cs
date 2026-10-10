// Velotext: registers global hotkeys and reports which ones another application already owns.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Velotext.Core.Model;
using Velotext.Platform.Windows.Native;
using Velotext.Platform.Windows.Threading;

namespace Velotext.Platform.Windows.Hotkeys;

/// <summary>A hotkey to register, with the key the caller uses to recognise it when pressed.</summary>
/// <param name="Key">Caller-defined identifier (for example a phrase id).</param>
/// <param name="Gesture">The key combination.</param>
public sealed record HotkeyRegistration(Guid Key, HotkeyGesture Gesture);

/// <summary>
/// Registers system-wide hotkeys with <c>RegisterHotKey</c>, which needs no admin rights. Registration
/// fails when another application (or Windows) already owns the combination; those are reported so
/// the UI can tell the user. Presses arrive on a background thread through <see cref="Pressed"/>.
/// </summary>
public sealed partial class GlobalHotkeyService : IDisposable
{
    private readonly ILogger<GlobalHotkeyService> _logger;
    private readonly MessageLoopThread _thread;
    private readonly Dictionary<int, Guid> _registered = [];

    /// <summary>Starts the hotkey thread.</summary>
    /// <param name="logger">Diagnostics logger.</param>
    public GlobalHotkeyService(ILogger<GlobalHotkeyService> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _thread = new MessageLoopThread("Hotkeys", ThreadPriority.AboveNormal, OnWindowMessage, ex => LogError(_logger, ex));
    }

    /// <summary>Raised on the hotkey thread with the registration key when a hotkey is pressed.</summary>
    public event EventHandler<Guid>? Pressed;

    /// <summary>Replaces all registered hotkeys.</summary>
    /// <param name="registrations">The hotkeys to register. Invalid gestures are skipped.</param>
    /// <returns>The gestures that could not be registered because something else owns them.</returns>
    public Task<IReadOnlyList<HotkeyGesture>> ReplaceAllAsync(IEnumerable<HotkeyRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var list = registrations.Where(r => r.Gesture.ValidationError is null).ToList();
        return _thread.InvokeAsync<IReadOnlyList<HotkeyGesture>>(() =>
        {
            foreach (var id in _registered.Keys)
            {
                NativeMethods.UnregisterHotKey(_thread.WindowHandle, id);
            }

            _registered.Clear();
            var failed = new List<HotkeyGesture>();
            var nextId = 1;
            foreach (var registration in list)
            {
                var id = nextId++;
                var modifiers = (uint)registration.Gesture.Modifiers | NativeMethods.ModNoRepeat;
                if (NativeMethods.RegisterHotKey(_thread.WindowHandle, id, modifiers, (uint)registration.Gesture.Key))
                {
                    _registered[id] = registration.Key;
                }
                else
                {
                    failed.Add(registration.Gesture);
                }
            }

            LogRegistered(_logger, _registered.Count, failed.Count);
            return failed;
        });
    }

    /// <summary>Unregisters everything and stops the thread.</summary>
    public void Dispose()
    {
        try
        {
            ReplaceAllAsync([]).Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The thread already ended; Windows releases its hotkeys.
        }

        _thread.Dispose();
    }

    private nint? OnWindowMessage(uint message, nint wParam, nint lParam)
    {
        if (message != NativeMethods.WmHotkey)
        {
            return null;
        }

        if (_registered.TryGetValue((int)wParam, out var key))
        {
            Pressed?.Invoke(this, key);
        }

        return 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Registered {Count} hotkeys; {Failed} already in use elsewhere.")]
    private static partial void LogRegistered(ILogger logger, int count, int failed);

    [LoggerMessage(Level = LogLevel.Error, Message = "Hotkey thread error.")]
    private static partial void LogError(ILogger logger, Exception exception);
}
