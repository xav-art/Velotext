// Velotext: a dedicated thread with a Win32 message loop and a hidden message-only window.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Velotext.Platform.Windows.Native;

namespace Velotext.Platform.Windows.Threading;

/// <summary>Handles a window message; returns a result when handled, or <c>null</c> to let Windows handle it.</summary>
/// <param name="message">The message id.</param>
/// <param name="wParam">First message parameter.</param>
/// <param name="lParam">Second message parameter.</param>
internal delegate nint? WindowMessageHandler(uint message, nint wParam, nint lParam);

/// <summary>
/// A background thread that runs a Win32 message loop and owns a message-only window. Low-level hooks,
/// clipboard ownership and hotkeys all need such a thread. Work can be marshalled onto it with
/// <see cref="Post"/> and <see cref="InvokeAsync{T}(Func{T})"/>.
/// </summary>
internal sealed class MessageLoopThread : IDisposable
{
    private const uint WmInvoke = NativeMethods.WmApp + 1;

    private readonly ConcurrentQueue<Action> _queue = new();
    private readonly Thread _thread;
    private readonly WndProc _wndProc;
    private readonly string _className;
    private readonly WindowMessageHandler? _handler;
    private readonly Action<Exception> _onError;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _hwnd;
    private uint _threadId;
    private volatile bool _disposed;

    /// <summary>Starts the thread and waits until its window exists.</summary>
    /// <param name="name">Thread name, for diagnostics.</param>
    /// <param name="priority">Thread priority.</param>
    /// <param name="handler">Optional handler for window messages.</param>
    /// <param name="onError">Receives exceptions thrown by posted work or the handler.</param>
    /// <param name="onStarted">Runs on the new thread right after the window is created.</param>
    public MessageLoopThread(string name, ThreadPriority priority, WindowMessageHandler? handler, Action<Exception> onError, Action<MessageLoopThread>? onStarted = null)
    {
        _handler = handler;
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
        _wndProc = WindowProcedure;
        _className = "Velotext." + name.Replace(' ', '.') + "." + Guid.NewGuid().ToString("N");
        _thread = new Thread(() => Run(onStarted)) { IsBackground = true, Name = name, Priority = priority };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _started.Task.GetAwaiter().GetResult();
    }

    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>The message-only window owned by the thread.</summary>
    public nint WindowHandle => _hwnd;

    /// <summary>True when called from the loop thread itself.</summary>
    public bool IsCurrentThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>Queues work to run on the loop thread. Ignored after disposal.</summary>
    /// <param name="action">The work.</param>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_disposed)
        {
            return;
        }

        _queue.Enqueue(action);
        NativeMethods.PostMessageW(_hwnd, WmInvoke, 0, 0);
    }

    /// <summary>Runs work on the loop thread and returns its result.</summary>
    /// <typeparam name="T">Result type.</typeparam>
    /// <param name="func">The work.</param>
    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        if (IsCurrentThread)
        {
            return Task.FromResult(func());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed)
        {
            completion.SetException(new ObjectDisposedException(nameof(MessageLoopThread)));
            return completion.Task;
        }

        Post(() =>
        {
            try
            {
                completion.SetResult(func());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        return completion.Task;
    }

    /// <summary>Runs work on the loop thread.</summary>
    /// <param name="action">The work.</param>
    public Task InvokeAsync(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync(() =>
        {
            action();
            return true;
        });
    }

    /// <summary>Stops the loop, destroys the window and waits briefly for the thread to end.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeMethods.PostThreadMessageW(_threadId, NativeMethods.WmQuit, 0, 0);
        if (!IsCurrentThread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private void Run(Action<MessageLoopThread>? onStarted)
    {
        var classNamePointer = Marshal.StringToHGlobalUni(_className);
        var instance = NativeMethods.GetModuleHandleW(null);
        try
        {
            _threadId = NativeMethods.GetCurrentThreadId();
            var windowClass = new WndClassEx
            {
                Size = (uint)Marshal.SizeOf<WndClassEx>(),
                WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                Instance = instance,
                ClassName = classNamePointer,
            };
            if (NativeMethods.RegisterClassExW(windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "RegisterClassEx failed.");
            }

            _hwnd = NativeMethods.CreateWindowExW(0, _className, _className, 0, 0, 0, 0, 0, NativeMethods.HwndMessage, 0, instance, 0);
            if (_hwnd == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateWindowEx failed.");
            }

            onStarted?.Invoke(this);
            _started.SetResult();
        }
        catch (Exception ex)
        {
            _started.TrySetException(ex);
            Marshal.FreeHGlobal(classNamePointer);
            return;
        }

        try
        {
            while (NativeMethods.GetMessageW(out var message, 0, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(message);
                NativeMethods.DispatchMessageW(message);
            }
        }
        finally
        {
            _disposed = true;
            NativeMethods.DestroyWindow(_hwnd);
            NativeMethods.UnregisterClassW(_className, instance);
            Marshal.FreeHGlobal(classNamePointer);
        }
    }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (message == WmInvoke)
            {
                while (_queue.TryDequeue(out var action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        _onError(ex);
                    }
                }

                return 0;
            }

            if (_handler?.Invoke(message, wParam, lParam) is { } result)
            {
                return result;
            }
        }
        catch (Exception ex)
        {
            // An exception must never cross back into Windows.
            _onError(ex);
        }

        return NativeMethods.DefWindowProcW(hwnd, message, wParam, lParam);
    }
}
