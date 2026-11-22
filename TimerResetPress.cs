using System;

namespace BeaconLog;

internal sealed class TimerResetPress
{
    private static readonly TimeSpan HoldDuration = TimeSpan.FromMilliseconds(500);
    private bool cancelled;
    private bool fired;

    internal bool Active { get; private set; }
    internal double Progress { get; private set; }

    internal void Begin()
    {
        Active = true;
        cancelled = false;
        fired = false;
        Progress = 0;
    }

    internal bool Advance(TimeSpan elapsed)
    {
        if (!Active || cancelled || fired)
        {
            return false;
        }

        Progress = Math.Clamp(elapsed / HoldDuration, 0, 1);
        fired = elapsed >= HoldDuration;
        return fired;
    }

    internal void Cancel()
    {
        if (Active)
        {
            cancelled = true;
            Progress = 0;
        }
    }

    internal bool End()
    {
        var consumeClick = Active && (cancelled || fired);
        Active = false;
        Progress = 0;
        return consumeClick;
    }
}
