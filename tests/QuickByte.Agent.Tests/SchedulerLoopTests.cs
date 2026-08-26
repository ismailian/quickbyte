using QuickByte.Core.Enums;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Agent.Tests;

/// <summary>
/// The agent's one decision: for each queue in queues.json, at this moment, do
/// nothing / stand down / start QuickByte.
///
/// Every test here fixes the clock rather than reading it. That is not just
/// determinism: "is this queue due?" is a question about a stated time — it is
/// why <see cref="QueueSchedule"/> takes <c>now</c> as a parameter, and the loop
/// takes its clock the same way so the same reasoning can be checked here.
///
/// Nothing in this file starts a process. Whether a due queue is launched, stood
/// down on, or left for the next tick is the whole of what the agent does, and
/// it has to be checkable without QuickByte on the machine — hence
/// <see cref="IAppLauncher"/>.
/// </summary>
public sealed class SchedulerLoopTests : IDisposable
{
    /// <summary>2026-08-26 is a Wednesday — the same anchor the Core suite's schedule tests use.</summary>
    private static readonly DateTime Wednesday = new(2026, 8, 26, 0, 0, 0, DateTimeKind.Local);

    private static readonly TimeSpan TwoAm = TimeSpan.FromHours(2);

    private readonly FakeQueueRepository _repository = new();
    private readonly FakeLauncher _app = new();
    private readonly LogFile _log = new();

    /// <summary>Half an hour into a 02:00 window, unless a test moves it.</summary>
    private DateTime _now = Wednesday.Add(TwoAm).AddMinutes(30);

    public void Dispose() => _log.Dispose();

    private SchedulerLoop Loop() => new(_repository, _app, () => _now);

    private DownloadQueue Scheduled(string name = "Nightly", TimeSpan? startTime = null)
    {
        var queue = new DownloadQueue
        {
            Name = name,
            Schedule = new QueueSchedule
            {
                Enabled = true,
                Days = ScheduleDays.EveryDay,
                StartTime = startTime ?? TwoAm
            }
        };
        _repository.Queues.Add(queue);
        return queue;
    }

    // ------------------------------------------------- reading the file --

    [Fact]
    public void An_unreadable_queue_file_is_not_an_empty_one()
    {
        // The app rewrites queues.json in place, so a read can lose a race with
        // it. Treating that as "no queues" would end the agent — the user's
        // scheduler would vanish because a file was busy for a millisecond.
        _repository.Readable = false;

        Assert.Equal(SchedulerLoop.TickOutcome.Waiting, Loop().EvaluateOnce());
        Assert.Empty(_app.Launched);
    }

    [Fact]
    public void An_unreadable_queue_file_is_complained_about_once()
    {
        _repository.Readable = false;
        var loop = Loop();

        loop.EvaluateOnce();
        loop.EvaluateOnce();
        loop.EvaluateOnce();

        // Once per outage, not once per tick: at a tick every 30 seconds, the
        // alternative fills the log — and blows its cap — overnight.
        Assert.Equal(1, _log.Count("could not be read"));
    }

    [Fact]
    public void An_outage_that_comes_back_and_returns_is_complained_about_again()
    {
        Scheduled();
        var loop = Loop();

        _repository.Readable = false;
        loop.EvaluateOnce();
        _repository.Readable = true;
        loop.EvaluateOnce();
        _repository.Readable = false;
        loop.EvaluateOnce();

        Assert.Equal(2, _log.Count("could not be read"));
    }

    [Fact]
    public void An_empty_queue_file_leaves_nothing_to_watch()
    {
        Assert.Equal(SchedulerLoop.TickOutcome.NothingToWatch, Loop().EvaluateOnce());
    }

    [Fact]
    public void A_queue_whose_schedule_is_switched_off_is_nothing_to_watch()
    {
        // The agent exits when this happens, so an unscheduled queue must read
        // as "no reason to exist" rather than as a queue that never comes due.
        var queue = Scheduled();
        queue.Schedule.Enabled = false;

        Assert.Equal(SchedulerLoop.TickOutcome.NothingToWatch, Loop().EvaluateOnce());
    }

    [Fact]
    public void The_agent_never_writes_the_queue_file()
    {
        // The app is the only writer — the fake throws if this is ever called,
        // so a pass that starts a queue proves it here.
        Scheduled();

        Assert.Equal(SchedulerLoop.TickOutcome.Waiting, Loop().EvaluateOnce());
        Assert.Single(_app.Launched);
    }

    // --------------------------------------------------- deciding to run --

    [Fact]
    public void A_queue_whose_time_has_not_come_is_left_alone()
    {
        Scheduled();
        _now = Wednesday.AddHours(1);

        Assert.Equal(SchedulerLoop.TickOutcome.Waiting, Loop().EvaluateOnce());
        Assert.Empty(_app.Launched);
    }

    [Fact]
    public void A_due_queue_starts_QuickByte()
    {
        var queue = Scheduled();

        Loop().EvaluateOnce();

        Assert.Equal(queue.Id, Assert.Single(_app.Launched));
        Assert.Equal(1, _log.Count("started QuickByte for queue 'Nightly'"));
    }

    [Fact]
    public void Only_the_queue_that_is_due_is_started()
    {
        var nightly = Scheduled("Nightly");
        Scheduled("Evening", TimeSpan.FromHours(23));

        Loop().EvaluateOnce();

        Assert.Equal(nightly.Id, Assert.Single(_app.Launched));
    }

    [Fact]
    public void Two_queues_due_at_once_each_get_their_own_launch()
    {
        var first = Scheduled("Nightly");
        var second = Scheduled("Also nightly");

        Loop().EvaluateOnce();

        Assert.Equal(new[] { first.Id, second.Id }, _app.Launched);
    }

    [Fact]
    public void A_queue_the_app_already_ran_is_left_alone()
    {
        // IsDue is Core's answer, and LastRunAt is how the app records that the
        // window has been served. The agent must defer to it or a queue the user
        // started by hand at 02:01 is started again at 02:30.
        var queue = Scheduled();
        queue.LastRunAt = new DateTimeOffset(Wednesday.Add(TwoAm));

        Loop().EvaluateOnce();

        Assert.Empty(_app.Launched);
    }

    // ---------------------------------------------- not starting it twice --

    [Fact]
    public void A_queue_is_started_once_for_a_window()
    {
        Scheduled();
        var loop = Loop();

        loop.EvaluateOnce();
        _now = _now.AddMinutes(1);
        loop.EvaluateOnce();

        // The app writes LastRunAt only once it has actually started; between the
        // launch and that write there are ticks where the queue still reads as
        // due, and this is what covers them.
        Assert.Single(_app.Launched);
    }

    [Fact]
    public void The_next_window_starts_it_again()
    {
        var queue = Scheduled();
        var loop = Loop();

        loop.EvaluateOnce();
        _now = _now.AddDays(1);
        loop.EvaluateOnce();

        Assert.Equal(new[] { queue.Id, queue.Id }, _app.Launched);
    }

    [Fact]
    public void A_queue_that_stops_being_scheduled_is_forgotten()
    {
        // The guard is a dictionary in a process that runs for weeks, so entries
        // for queues that are no longer scheduled are dropped. The cost is that
        // switching a schedule off and on inside one window arms it again —
        // acceptable, because LastRunAt in queues.json is the real guard against
        // a double start, and this one only covers the seconds before it lands.
        var queue = Scheduled();
        Scheduled("Something else to watch");
        var loop = Loop();

        loop.EvaluateOnce();
        queue.Schedule.Enabled = false;
        loop.EvaluateOnce();
        queue.Schedule.Enabled = true;
        loop.EvaluateOnce();

        Assert.Equal(2, _app.Launched.Count(id => id == queue.Id));
    }

    // ------------------------------------------------- standing down and --
    // ------------------------------------------------------ trying again --

    [Fact]
    public void The_agent_stands_down_while_QuickByte_is_running()
    {
        // The app runs the same schedule check itself, and a second process
        // would only hand the queue straight back to it.
        Scheduled();
        _app.IsRunning = true;

        Loop().EvaluateOnce();

        Assert.Empty(_app.Launched);
        Assert.Equal(1, _log.Count("QuickByte is running"));
    }

    [Fact]
    public void Standing_down_is_not_recorded_as_a_start()
    {
        // QuickByte was up when the window opened and was closed a minute later,
        // before it got round to starting the queue. The window is still owed.
        Scheduled();
        var loop = Loop();

        _app.IsRunning = true;
        loop.EvaluateOnce();
        _app.IsRunning = false;
        _now = _now.AddMinutes(1);
        loop.EvaluateOnce();

        Assert.Single(_app.Launched);
    }

    [Fact]
    public void A_launch_that_did_not_take_is_tried_again()
    {
        var queue = Scheduled();
        var loop = Loop();

        _app.FailsWith = "the system cannot find the file specified";
        loop.EvaluateOnce();

        Assert.Empty(_app.Launched);
        Assert.Equal(1, _log.Count("could not start QuickByte"));

        _app.FailsWith = null;
        _now = _now.AddSeconds(30);
        loop.EvaluateOnce();

        Assert.Equal(queue.Id, Assert.Single(_app.Launched));
    }

    // ------------------------------------------------------- the loop --

    [Fact]
    public async Task The_loop_stops_as_soon_as_there_is_nothing_to_watch()
    {
        // A user who never schedules anything must not be left with a background
        // process — so this returns rather than sleeping on an empty file.
        await Loop().RunAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, _log.Count("scheduler started"));
        Assert.Equal(1, _log.Count("no queue has a schedule"));
    }

    [Fact]
    public async Task The_loop_stops_when_the_session_ends()
    {
        // Sign-out reaches this process as a plain exit, which cancels the token
        // mid-wait; the cancellation surfaces out of RunAsync for Program to
        // swallow. The fake cancels from inside the read so the test does not
        // sit through a 30-second tick.
        Scheduled();
        using var cancellation = new CancellationTokenSource();
        _repository.OnLoad = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Loop().RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Single(_app.Launched);
    }

    // ------------------------------------------------------------ fakes --

    private sealed class FakeQueueRepository : IQueueRepository
    {
        public List<DownloadQueue> Queues { get; } = new();

        /// <summary>Whether queues.json can be read this pass. False is the app mid-write.</summary>
        public bool Readable { get; set; } = true;

        /// <summary>Runs inside the read, for a test that needs to interrupt the loop.</summary>
        public Action? OnLoad { get; set; }

        public List<DownloadQueue> LoadAll() => Queues.Select(queue => queue.Clone()).ToList();

        public bool TryLoadAll(out List<DownloadQueue> queues)
        {
            OnLoad?.Invoke();

            // Clones, like the real repository: the loop must not be able to edit
            // what the next pass reads.
            queues = Readable ? Queues.Select(queue => queue.Clone()).ToList() : new List<DownloadQueue>();
            return Readable;
        }

        public void SaveAll(IEnumerable<DownloadQueue> queues) =>
            throw new InvalidOperationException(
                "the agent must never write queues.json — the app is its only writer");
    }

    private sealed class FakeLauncher : IAppLauncher
    {
        public bool IsRunning { get; set; }

        /// <summary>Non-null makes the launch fail with this message, as a refused start would.</summary>
        public string? FailsWith { get; set; }

        public List<Guid> Launched { get; } = new();

        public bool TryLaunchForQueue(Guid queueId, out string? error)
        {
            if (FailsWith is not null)
            {
                error = FailsWith;
                return false;
            }

            Launched.Add(queueId);
            error = null;
            return true;
        }
    }
}
