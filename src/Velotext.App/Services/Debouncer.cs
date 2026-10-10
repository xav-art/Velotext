// Velotext: runs an action once after a burst of requests has gone quiet (used for saving).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

namespace Velotext.App.Services;

/// <summary>
/// Runs an action on a thread-pool thread once no new request has arrived for <c>delay</c>.
/// <see cref="Flush"/> runs a pending action at once, for example before the application exits.
/// </summary>
public sealed class Debouncer : IDisposable
{
    private readonly Action _action;
    private readonly TimeSpan _delay;
    private readonly Timer _timer;
    private readonly Lock _gate = new();
    private bool _pending;

    /// <summary>Creates the debouncer.</summary>
    /// <param name="action">The action to run.</param>
    /// <param name="delay">Quiet period before it runs.</param>
    public Debouncer(Action action, TimeSpan delay)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
        _delay = delay;
        _timer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>True while a run is scheduled.</summary>
    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _pending;
            }
        }
    }

    /// <summary>Schedules a run, restarting the quiet period.</summary>
    public void Request()
    {
        lock (_gate)
        {
            _pending = true;
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Runs the action now if a run is pending.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_pending)
            {
                return;
            }

            _pending = false;
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        _action();
    }

    /// <summary>Runs any pending action and stops the timer.</summary>
    public void Dispose()
    {
        Flush();
        _timer.Dispose();
    }
}
