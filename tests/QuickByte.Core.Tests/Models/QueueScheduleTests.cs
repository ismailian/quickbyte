using QuickByte.Core.Enums;
using QuickByte.Core.Models;

namespace QuickByte.Core.Tests.Models;

/// <summary>
/// The schedule is a <em>window</em>, not an instant, and every method takes the
/// current time as a parameter rather than reading the clock. That is what lets
/// the in-app scheduler and the out-of-process agent reach the same verdict from
/// the same file — and it is what makes any of this testable.
/// </summary>
public sealed class QueueScheduleTests
{
    // 2026-08-26 is a Wednesday.
    private static readonly DateTime Wednesday = new(2026, 8, 26, 0, 0, 0, DateTimeKind.Local);

    private static QueueSchedule At(TimeSpan start, ScheduleDays days = ScheduleDays.EveryDay) => new()
    {
        Enabled = true,
        Days = days,
        StartTime = start
    };

    [Fact]
    public void A_disabled_schedule_is_never_in_a_window()
    {
        var schedule = At(TimeSpan.FromHours(2));
        schedule.Enabled = false;

        Assert.Null(schedule.WindowStart(Wednesday.AddHours(2)));
        Assert.Null(schedule.NextStart(Wednesday));
    }

    [Fact]
    public void A_schedule_with_no_days_never_fires()
    {
        var schedule = At(TimeSpan.FromHours(2), ScheduleDays.None);

        Assert.Null(schedule.WindowStart(Wednesday.AddHours(2)));
        Assert.Null(schedule.NextStart(Wednesday));
    }

    [Fact]
    public void WindowStart_is_the_start_time_of_the_window_now_falls_in()
    {
        var schedule = At(TimeSpan.FromHours(2));

        Assert.Equal(Wednesday.AddHours(2), schedule.WindowStart(Wednesday.AddHours(2).AddMinutes(30)));
    }

    [Fact]
    public void WindowStart_is_null_before_the_start_time_arrives()
    {
        var schedule = At(TimeSpan.FromHours(2));

        Assert.Null(schedule.WindowStart(Wednesday.AddHours(1)));
    }

    [Fact]
    public void A_missed_run_is_honoured_inside_the_grace_period()
    {
        // Signing in at 09:05 honours an 08:30 schedule.
        var schedule = At(TimeSpan.FromHours(8.5));

        Assert.Equal(Wednesday.AddHours(8.5), schedule.WindowStart(Wednesday.AddHours(9).AddMinutes(5)));
    }

    [Fact]
    public void A_missed_run_is_not_honoured_once_the_grace_period_has_passed()
    {
        // One at 11:00 does not. Without the bound, the first launch of the day
        // would start every schedule the week had missed.
        var schedule = At(TimeSpan.FromHours(8.5));

        Assert.Null(schedule.WindowStart(Wednesday.AddHours(11)));
        Assert.Equal(TimeSpan.FromHours(1), QueueSchedule.MissedRunGrace);
    }

    [Fact]
    public void A_window_may_cross_midnight()
    {
        // 22:00 until 06:00, written without a second date field.
        var schedule = At(TimeSpan.FromHours(22));
        schedule.StopAtEnabled = true;
        schedule.StopTime = TimeSpan.FromHours(6);

        // At 01:00 on Thursday the run that matters is Wednesday's 22:00 one —
        // Thursday's own start time has not arrived yet.
        var thursdayEarly = Wednesday.AddDays(1).AddHours(1);

        Assert.Equal(Wednesday.AddHours(22), schedule.WindowStart(thursdayEarly));
    }

    [Fact]
    public void A_window_that_crossed_midnight_closes_at_its_stop_time()
    {
        var schedule = At(TimeSpan.FromHours(22));
        schedule.StopAtEnabled = true;
        schedule.StopTime = TimeSpan.FromHours(6);

        Assert.Null(schedule.WindowStart(Wednesday.AddDays(1).AddHours(7)));
    }

    [Fact]
    public void WindowEnd_is_the_grace_period_when_no_stop_time_is_set()
    {
        var schedule = At(TimeSpan.FromHours(2));
        var start = Wednesday.AddHours(2);

        Assert.Equal(start + QueueSchedule.MissedRunGrace, schedule.WindowEnd(start));
    }

    [Fact]
    public void WindowEnd_pushes_a_stop_time_at_or_before_the_start_to_the_next_day()
    {
        var schedule = At(TimeSpan.FromHours(22));
        schedule.StopAtEnabled = true;
        schedule.StopTime = TimeSpan.FromHours(6);

        Assert.Equal(Wednesday.AddDays(1).AddHours(6), schedule.WindowEnd(Wednesday.AddHours(22)));
    }

    [Fact]
    public void A_window_is_confined_to_the_days_it_runs_on()
    {
        var schedule = At(TimeSpan.FromHours(2), ScheduleDays.Weekend);

        // Wednesday is not a weekend day, and neither is the Tuesday before it.
        Assert.Null(schedule.WindowStart(Wednesday.AddHours(2).AddMinutes(30)));
    }

    [Fact]
    public void NextStart_is_later_today_when_the_time_has_not_come()
    {
        var schedule = At(TimeSpan.FromHours(22));

        Assert.Equal(Wednesday.AddHours(22), schedule.NextStart(Wednesday.AddHours(10)));
    }

    [Fact]
    public void NextStart_is_tomorrow_once_today_is_behind_us()
    {
        var schedule = At(TimeSpan.FromHours(2));

        Assert.Equal(Wednesday.AddDays(1).AddHours(2), schedule.NextStart(Wednesday.AddHours(10)));
    }

    [Fact]
    public void NextStart_looks_a_full_week_ahead()
    {
        // Today's occurrence is already behind us, so the same weekday a week out
        // is the answer — which is why the search runs to eight days, not seven.
        var schedule = At(TimeSpan.FromHours(2), ScheduleDays.Wednesday);

        Assert.Equal(Wednesday.AddDays(7).AddHours(2), schedule.NextStart(Wednesday.AddHours(10)));
    }

    [Fact]
    public void NextStart_is_strictly_after_the_moment_it_is_given()
    {
        var schedule = At(TimeSpan.FromHours(2));

        Assert.Equal(Wednesday.AddDays(1).AddHours(2), schedule.NextStart(Wednesday.AddHours(2)));
    }

    [Theory]
    [InlineData(DayOfWeek.Sunday, ScheduleDays.Sunday)]
    [InlineData(DayOfWeek.Monday, ScheduleDays.Monday)]
    [InlineData(DayOfWeek.Saturday, ScheduleDays.Saturday)]
    public void ToFlag_derives_the_flag_by_shifting_a_DayOfWeek(DayOfWeek day, ScheduleDays expected) =>
        Assert.Equal(expected, QueueSchedule.ToFlag(day));

    [Fact]
    public void RunsOn_reads_the_flags()
    {
        var weekdays = At(TimeSpan.Zero, ScheduleDays.Weekdays);

        Assert.True(weekdays.RunsOn(DayOfWeek.Monday));
        Assert.True(weekdays.RunsOn(DayOfWeek.Friday));
        Assert.False(weekdays.RunsOn(DayOfWeek.Saturday));
        Assert.False(weekdays.RunsOn(DayOfWeek.Sunday));
    }

    [Fact]
    public void The_day_groups_add_up()
    {
        Assert.Equal(ScheduleDays.EveryDay, ScheduleDays.Weekdays | ScheduleDays.Weekend);
        Assert.Equal(ScheduleDays.Weekend, ScheduleDays.Saturday | ScheduleDays.Sunday);

        var everyDay = At(TimeSpan.Zero);
        Assert.All(Enum.GetValues<DayOfWeek>(), day => Assert.True(everyDay.RunsOn(day)));
    }

    [Fact]
    public void Clone_is_detached_from_the_schedule_the_runner_reads()
    {
        var original = At(TimeSpan.FromHours(2), ScheduleDays.Weekdays);
        original.StopAtEnabled = true;
        original.StopTime = TimeSpan.FromHours(6);

        var copy = original.Clone();
        copy.Enabled = false;
        copy.StartTime = TimeSpan.FromHours(23);
        copy.Days = ScheduleDays.None;

        Assert.True(original.Enabled);
        Assert.Equal(TimeSpan.FromHours(2), original.StartTime);
        Assert.Equal(ScheduleDays.Weekdays, original.Days);
    }
}
