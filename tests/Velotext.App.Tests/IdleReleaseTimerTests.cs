// Velotext: tests for the idle release timer that frees the hidden main window (start, cancel, restart, late firing).
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using Velotext.App.Services;
using Velotext.App.ViewModels;

namespace Velotext.App.Tests;

/// <summary>Behaviour of <see cref="IdleReleaseTimer"/> with a timer the test fires by hand.</summary>
public sealed class IdleReleaseTimerTests
{
    private static readonly TimeSpan s_delay = TimeSpan.FromMinutes(2);
    private readonly ManualTimers _time = new();
    private readonly QueuedDispatcher _ui = new();
    private int _releases;

    private IdleReleaseTimer Create() => new(s_delay, () => _releases++, _ui, _time);

    [Fact]
    public void Releases_on_the_ui_thread_once_the_delay_has_passed()
    {
        using var timer = Create();
        Assert.False(timer.IsArmed);
        Assert.Equal(Timeout.InfiniteTimeSpan, _time.Timer.DueTime);

        timer.Start();
        Assert.True(timer.IsArmed);
        Assert.Equal(s_delay, _time.Timer.DueTime);

        _time.Timer.Fire();
        Assert.Equal(0, _releases);
        _ui.RunAll();

        Assert.Equal(1, _releases);
        Assert.False(timer.IsArmed);
    }

    [Fact]
    public void Cancel_stops_the_countdown()
    {
        using var timer = Create();
        timer.Start();

        timer.Cancel();
        _time.Timer.Fire();
        _ui.RunAll();

        Assert.Equal(0, _releases);
        Assert.False(timer.IsArmed);
        Assert.Equal(Timeout.InfiniteTimeSpan, _time.Timer.DueTime);
    }

    [Fact]
    public void Shown_again_after_the_timer_fired_but_before_the_ui_thread_ran_is_not_released()
    {
        using var timer = Create();
        timer.Start();
        _time.Timer.Fire();

        timer.Cancel();
        _ui.RunAll();

        Assert.Equal(0, _releases);
    }

    [Fact]
    public void Restarted_after_the_timer_fired_waits_for_the_new_countdown()
    {
        using var timer = Create();
        timer.Start();
        _time.Timer.Fire();

        timer.Start();
        _ui.RunAll();
        Assert.Equal(0, _releases);
        Assert.True(timer.IsArmed);

        _time.Timer.Fire();
        _ui.RunAll();
        Assert.Equal(1, _releases);
    }

    [Fact]
    public void Dispose_stops_the_timer_and_checks_arguments()
    {
        var timer = Create();
        timer.Start();

        timer.Dispose();

        Assert.True(_time.Timer.Disposed);
        Assert.False(timer.IsArmed);
        Assert.Throws<ArgumentNullException>(() => new IdleReleaseTimer(s_delay, null!, _ui, _time));
        Assert.Throws<ArgumentNullException>(() => new IdleReleaseTimer(s_delay, () => { }, null!, _time));
        Assert.Throws<ArgumentNullException>(() => new IdleReleaseTimer(s_delay, () => { }, _ui, null!));
    }

    /// <summary>Holds UI work until the test runs it, like a busy UI thread.</summary>
    private sealed class QueuedDispatcher : IUiDispatcher
    {
        private readonly Queue<Action> _work = new();

        public void Post(Action action) => _work.Enqueue(action);

        public void RunAll()
        {
            while (_work.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    /// <summary>A clock whose single timer only fires when the test says so.</summary>
    private sealed class ManualTimers : TimeProvider
    {
        public ManualTimer Timer { get; private set; } = null!;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer = new ManualTimer(() => callback(state)) { DueTime = dueTime };
            return Timer;
        }
    }

    /// <summary>A timer that remembers its due time and fires on request.</summary>
    private sealed class ManualTimer(Action fire) : ITimer
    {
        public TimeSpan DueTime { get; set; }

        public bool Disposed { get; private set; }

        public void Fire() => fire();

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            DueTime = dueTime;
            return true;
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
