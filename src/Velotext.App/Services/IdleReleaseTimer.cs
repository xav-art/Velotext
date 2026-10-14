// Velotext: runs a release action on the UI thread once something has stayed unused (for example hidden) for a while.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.App.ViewModels;

namespace Velotext.App.Services;

/// <summary>
/// Counts down from <see cref="Start"/> and runs the release action on the UI thread when the delay has
/// passed. <see cref="Cancel"/> stops it, and a countdown that was cancelled or restarted after the timer
/// fired never runs the action, so a window shown again at the last moment is never released.
/// </summary>
public sealed class IdleReleaseTimer : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly Action _release;
    private readonly IUiDispatcher _ui;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private long _generation;
    private bool _armed;

    /// <summary>Creates the timer, stopped.</summary>
    /// <param name="delay">How long the thing must stay unused before it is released.</param>
    /// <param name="release">The release action; it runs on the UI thread.</param>
    /// <param name="ui">Puts the release on the UI thread.</param>
    /// <param name="time">Clock and timers.</param>
    public IdleReleaseTimer(TimeSpan delay, Action release, IUiDispatcher ui, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _delay = delay;
        _release = release ?? throw new ArgumentNullException(nameof(release));
        _ui = ui ?? throw new ArgumentNullException(nameof(ui));
        _timer = time.CreateTimer(_ => OnElapsed(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>True while a countdown runs.</summary>
    public bool IsArmed
    {
        get
        {
            lock (_gate)
            {
                return _armed;
            }
        }
    }

    /// <summary>Starts the countdown, or starts it again from the full delay.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _armed = true;
            _generation++;
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Stops the countdown; the release does not run.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _armed = false;
            _generation++;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Stops the timer for good.</summary>
    public void Dispose()
    {
        Cancel();
        _timer.Dispose();
    }

    private void OnElapsed()
    {
        long generation;
        lock (_gate)
        {
            if (!_armed)
            {
                return;
            }

            generation = _generation;
        }

        _ui.Post(() =>
        {
            lock (_gate)
            {
                // Shown again (or restarted) between the timer firing and the UI thread getting here.
                if (!_armed || generation != _generation)
                {
                    return;
                }

                _armed = false;
            }

            _release();
        });
    }
}
