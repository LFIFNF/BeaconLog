using System;
using System.Runtime.InteropServices;

namespace BeaconLog;

internal readonly record struct SleepClockSample(ulong TotalTicks, ulong AwakeTicks)
{
    public static SleepClockSample? Read()
    {
        var totalTicks = GetTickCount64() * (ulong)TimeSpan.TicksPerMillisecond;
        return QueryUnbiasedInterruptTime(out var awakeTicks) ? new(totalTicks, awakeTicks) : null;
    }

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong awakeTicks);
}

internal sealed class SleepIntervalDetector
{
    private static readonly ulong ThresholdTicks = (ulong)TimeSpan.FromMinutes(60).Ticks;
    private SleepClockSample? previous;

    internal void Rebase(SleepClockSample? sample) => previous = sample;

    internal bool Observe(SleepClockSample? sample)
    {
        if (sample is not { } current)
        {
            return false;
        }

        if (previous is not { } before)
        {
            previous = current;
            return false;
        }
        if (current.TotalTicks < before.TotalTicks || current.AwakeTicks < before.AwakeTicks)
        {
            // A delayed recovery notification may contain a sample older than the timer's sample.
            return false;
        }

        previous = current;
        var total = current.TotalTicks - before.TotalTicks;
        var awake = current.AwakeTicks - before.AwakeTicks;
        return total > awake && total - awake > ThresholdTicks;
    }
}
