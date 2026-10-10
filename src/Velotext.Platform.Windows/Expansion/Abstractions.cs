// Velotext: seams between the expansion engine and Windows, so the engine can be unit tested.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.Core.Input;
using Velotext.Platform.Windows.Clipboard;
using Velotext.Platform.Windows.Interop;
using Velotext.Platform.Windows.Native;

namespace Velotext.Platform.Windows.Expansion;

/// <summary>Clipboard operations used by the engine.</summary>
public interface IClipboardService
{
    /// <summary>Saves the clipboard and offers a phrase for pasting into a target process.</summary>
    /// <param name="payload">The phrase formats.</param>
    /// <param name="targetProcessId">Process that should read it.</param>
    /// <returns>The paste session, or <c>null</c> when the clipboard could not be opened.</returns>
    Task<PasteSession?> BeginPasteAsync(ClipboardPayload payload, uint targetProcessId);

    /// <summary>Restores the saved clipboard of a finished paste.</summary>
    /// <param name="session">The finished paste session.</param>
    Task RestoreAsync(PasteSession session);

    /// <summary>Copies a phrase to the clipboard.</summary>
    /// <param name="payload">The phrase formats.</param>
    /// <returns>True when the clipboard was updated.</returns>
    Task<bool> CopyAsync(ClipboardPayload payload);
}

/// <summary>Gives access to the input sequencer on the thread that owns it.</summary>
public interface ISequencerHost
{
    /// <summary>Runs an action against the sequencer on its thread.</summary>
    /// <param name="action">The action.</param>
    void Post(Action<InputSequencer> action);

    /// <summary>Runs a function against the sequencer on its thread and returns the result.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="func">The function.</param>
    Task<T> InvokeAsync<T>(Func<InputSequencer, T> func);
}

/// <summary>Read-only system state the engine consults.</summary>
public interface ISystemState
{
    /// <summary>The focused window and its process.</summary>
    ForegroundTarget Foreground();

    /// <summary>True while the key is physically held down.</summary>
    /// <param name="virtualKey">Virtual-key code.</param>
    bool IsKeyDown(int virtualKey);

    /// <summary>The window that has the clipboard open, or zero.</summary>
    nint OpenClipboardWindow();
}

/// <summary>The real system state.</summary>
public sealed class WindowsSystemState : ISystemState
{
    /// <inheritdoc />
    public ForegroundTarget Foreground() => ForegroundTarget.Current();

    /// <inheritdoc />
    public bool IsKeyDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <inheritdoc />
    public nint OpenClipboardWindow() => NativeMethods.GetOpenClipboardWindow();
}
