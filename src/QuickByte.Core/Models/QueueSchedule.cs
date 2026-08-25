using QuickByte.Core.Enums;

namespace QuickByte.Core.Models;

/// <summary>
/// When a queue should start itself, and when it should stop. Persisted inside
/// <see cref="DownloadQueue"/> as part of queues.json.
///
/// Everything here is expressed in <em>local</em> wall-clock terms — "Tuesdays
/// at 02:00" — and every method takes the current local time as a parameter
/// rather than reading the clock itself. That is what makes the schedule
/// testable, and what lets the out-of-process scheduler agent
/// (<c>QuickByte.Agent</c>) reach exactly the same verdict as the running app
/// from the same file.
///
/// The model is a <em>window</em>, not an instant. A schedule that only knew its
/// start time could only fire if something happened to be watching the clock at
/// that exact minute, and the whole point of the agent is that the machine may
/// have been asleep, or the user signed out, when 02:00 went past. A window has
/// an answer for "is this queue supposed to be running right now?", which is a
/// question anyone arriving late can still answer correctly.
/// </summary>
public sealed class QueueSchedule
{
    /// <summary>
    /// How long after its start time a queue with no stop time will still start
    /// if nobody was there to start it — a sign-in at 09:05 honours an 08:30
    /// schedule, one at 11:00 does not. Without a bound, the first launch of the
    /// day would start every schedule the week had missed.
    /// </summary>
    public static readonly TimeSpan MissedRunGrace = TimeSpan.FromHours(1);

    /// <summary>Master switch. Off means the queue only ever runs when told to by hand.</summary>
    public bool Enabled { get; set; }

    public ScheduleDays Days { get; set; } = ScheduleDays.EveryDay;

    /// <summary>Time of day the queue starts, as an offset into the local day.</summary>
    public TimeSpan StartTime { get; set; } = TimeSpan.FromHours(2);

    /// <summary>
    /// Whether the queue also stops at a set time. Off by default: the common
    /// case is "start overnight and let it finish".
    /// </summary>
    public bool StopAtEnabled { get; set; }

    /// <summary>
    /// Time of day a scheduled run is stopped. A value at or before
    /// <see cref="StartTime"/> means the next day, which is what makes the
    /// natural "22:00 until 06:00" work without a second date field.
    /// </summary>
    public TimeSpan StopTime { get; set; } = TimeSpan.FromHours(7);

    public bool RunsOn(DayOfWeek day) => (Days & ToFlag(day)) != 0;

    public static ScheduleDays ToFlag(DayOfWeek day) => (ScheduleDays)(1 << (int)day);

    /// <summary>
    /// The start instant of the window <paramref name="now"/> falls inside, or
    /// null if the queue is not supposed to be running at that moment.
    ///
    /// Yesterday is checked as well as today because a window is allowed to
    /// cross midnight: at 01:00 on Wednesday the run that matters is Tuesday's
    /// 22:00 one, and Wednesday's own start time has not arrived yet.
    /// </summary>
    public DateTime? WindowStart(DateTime now)
    {
        if (!Enabled || Days == ScheduleDays.None) return null;

        for (int daysBack = 0; daysBack <= 1; daysBack++)
        {
            DateTime date = now.Date.AddDays(-daysBack);
            if (!RunsOn(date.DayOfWeek)) continue;

            DateTime start = date + StartTime;
            if (now >= start && now < WindowEnd(start)) return start;
        }
        return null;
    }

    /// <summary>The instant a run started at <paramref name="start"/> is due to stop.</summary>
    public DateTime WindowEnd(DateTime start)
    {
        if (!StopAtEnabled) return start + MissedRunGrace;

        DateTime stop = start.Date + StopTime;
        if (stop <= start) stop = stop.AddDays(1);
        return stop;
    }

    /// <summary>
    /// The next start instant strictly after <paramref name="after"/>, or null
    /// if no day is selected. Used for the "next run" line in the queue window
    /// and to size the agent's sleep.
    /// </summary>
    public DateTime? NextStart(DateTime after)
    {
        if (!Enabled || Days == ScheduleDays.None) return null;

        // Eight days rather than seven: today's occurrence may already be behind
        // us, and the same weekday a week out is then the answer.
        for (int offset = 0; offset <= 7; offset++)
        {
            DateTime date = after.Date.AddDays(offset);
            if (!RunsOn(date.DayOfWeek)) continue;

            DateTime start = date + StartTime;
            if (start > after) return start;
        }
        return null;
    }

    public QueueSchedule Clone() => new()
    {
        Enabled = Enabled,
        Days = Days,
        StartTime = StartTime,
        StopAtEnabled = StopAtEnabled,
        StopTime = StopTime
    };
}
