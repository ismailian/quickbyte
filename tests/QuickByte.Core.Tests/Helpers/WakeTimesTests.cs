using System.Text.Json;
using QuickByte.Core.Enums;
using QuickByte.Core.Helpers;
using QuickByte.Core.Models;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// When the computer is woken: only for a queue that is scheduled, has days,
/// and asked to wake it — a machine switching itself on in the night is the one
/// thing here nobody should get without asking.
/// </summary>
public sealed class WakeTimesTests
{
    private static DownloadQueue Queue(TimeSpan start, ScheduleDays days = ScheduleDays.EveryDay, bool enabled = true, bool wake = true) => new()
    {
        Schedule = new QueueSchedule { Enabled = enabled, Days = days, StartTime = start, WakeComputer = wake }
    };

    [Fact]
    public void Only_a_scheduled_queue_that_asked_to_wake_the_computer_wakes_it()
    {
        var wakes = WakeTimes.For(new[]
        {
            Queue(TimeSpan.FromHours(1), wake: false),
            Queue(TimeSpan.FromHours(2), enabled: false),
            Queue(TimeSpan.FromHours(3), days: ScheduleDays.None),
            Queue(TimeSpan.FromHours(4), days: ScheduleDays.Weekend)
        });

        Assert.Equal(new[] { new WakeTime(ScheduleDays.Weekend, TimeSpan.FromHours(4)) }, wakes);
    }

    [Fact]
    public void Queues_starting_at_the_same_moment_share_a_wake_on_all_their_days()
    {
        var wakes = WakeTimes.For(new[]
        {
            Queue(new TimeSpan(3, 30, 0), ScheduleDays.Monday),
            Queue(new TimeSpan(1, 0, 0), ScheduleDays.Friday),
            Queue(new TimeSpan(0, 3, 30, 0, 400), ScheduleDays.Tuesday | ScheduleDays.Monday)
        });

        Assert.Equal(
            new[]
            {
                new WakeTime(ScheduleDays.Friday, new TimeSpan(1, 0, 0)),
                new WakeTime(ScheduleDays.Monday | ScheduleDays.Tuesday, new TimeSpan(3, 30, 0))
            },
            wakes);
    }

    [Fact]
    public void A_start_time_outside_the_day_from_a_hand_edited_file_is_read_as_midnight()
    {
        var wake = Assert.Single(WakeTimes.For(new[] { Queue(TimeSpan.FromHours(30)) }));

        Assert.Equal(TimeSpan.Zero, wake.At);
    }

    [Fact]
    public void No_more_wakes_than_one_task_can_hold()
    {
        var queues = Enumerable.Range(0, 100).Select(i => Queue(TimeSpan.FromMinutes(i * 7)));

        Assert.Equal(WakeTimes.MaxTriggers, WakeTimes.For(queues).Count);
    }

    [Fact]
    public void The_wake_choice_is_kept_with_the_schedule()
    {
        var schedule = new QueueSchedule { Enabled = true, WakeComputer = true };

        Assert.True(schedule.Clone().WakeComputer);
        Assert.True(JsonSerializer.Deserialize<QueueSchedule>(JsonSerializer.Serialize(schedule))!.WakeComputer);
        Assert.False(JsonSerializer.Deserialize<QueueSchedule>("{\"Enabled\":true}")!.WakeComputer);
    }

    [Fact]
    public void Keeping_the_computer_awake_is_on_unless_turned_off_even_for_an_older_settings_file()
    {
        Assert.True(new DownloadSettings().KeepAwakeWhileDownloading);
        Assert.True(JsonSerializer.Deserialize<DownloadSettings>("{}")!.KeepAwakeWhileDownloading);
        Assert.False(JsonSerializer.Deserialize<DownloadSettings>("{\"KeepAwakeWhileDownloading\":false}")!.KeepAwakeWhileDownloading);
    }
}
