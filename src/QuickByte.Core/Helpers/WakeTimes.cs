using QuickByte.Core.Enums;
using QuickByte.Core.Models;

namespace QuickByte.Core.Helpers;

/// <summary>A time of day, on some days of the week, at which the computer should be awake.</summary>
public readonly record struct WakeTime(ScheduleDays Days, TimeSpan At);

/// <summary>
/// The moments the computer has to be woken for — the start of every scheduled
/// queue that asked to wake it — reduced to as few as describe them all.
///
/// Waking is only needed at a window's start: <see cref="QueueSchedule"/> is a
/// window, and once the machine is awake the scheduler finds the queue due as it
/// would at any other moment inside it. Two queues starting at the same minute
/// share one wake, on the days of both.
/// </summary>
public static class WakeTimes
{
    /// <summary>Task Scheduler takes at most this many triggers on one task.</summary>
    public const int MaxTriggers = 48;

    public static IReadOnlyList<WakeTime> For(IEnumerable<DownloadQueue> queues) =>
        queues
            .Select(queue => queue.Schedule)
            .Where(schedule => schedule is { Enabled: true, WakeComputer: true } && (schedule.Days & ScheduleDays.EveryDay) != ScheduleDays.None)
            .GroupBy(schedule => Normalise(schedule.StartTime))
            .Select(group => new WakeTime(
                group.Aggregate(ScheduleDays.None, (days, schedule) => days | (schedule.Days & ScheduleDays.EveryDay)),
                group.Key))
            .OrderBy(wake => wake.At)
            .Take(MaxTriggers)
            .ToList();

    /// <summary>To the second, inside one day: a hand-edited queues.json can hold anything.</summary>
    private static TimeSpan Normalise(TimeSpan time)
    {
        if (time < TimeSpan.Zero || time >= TimeSpan.FromDays(1)) time = TimeSpan.Zero;
        return TimeSpan.FromSeconds(Math.Floor(time.TotalSeconds));
    }
}
