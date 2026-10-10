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
    /// <summary>
    /// Changes every time the caret may have moved (a click or a window change). An operation compares
    /// it with the value at its start and stops before typing into a place the user left.
    /// </summary>
    long ContextGeneration { get; }

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

    /// <summary>True when the window is a console or terminal, where Ctrl+Z does not undo (it can suspend a program).</summary>
    /// <param name="window">Window handle.</param>
    bool IsTerminal(nint window);
}

/// <summary>The real system state.</summary>
public sealed class WindowsSystemState : ISystemState
{
    /// <inheritdoc />
    public ForegroundTarget Foreground() => ForegroundTarget.Current();

    /// <inheritdoc />
    public bool IsKeyDown(int virtualKey) => (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static readonly HashSet<string> s_terminalClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "mintty", "PuTTY", "KiTTY", "VirtualConsoleClass",
        "Alacritty", "org.wezfurlong.wezterm",
    };

    /// <inheritdoc />
    public nint OpenClipboardWindow() => NativeMethods.GetOpenClipboardWindow();

    /// <inheritdoc />
    public unsafe bool IsTerminal(nint window)
    {
        if (window == 0)
        {
            return false;
        }

        var buffer = stackalloc char[256];
        var length = NativeMethods.GetClassNameW(window, buffer, 256);
        return length > 0 && s_terminalClasses.Contains(new string(buffer, 0, length));
    }
}
