using System.Diagnostics;
using System.Threading;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Agent;

/// <summary>
/// The agent's whole behaviour: wake up, ask each scheduled queue whether it is
/// due, and launch QuickByte for the ones that are.
///
/// "Due" is <see cref="DownloadQueue.IsDue"/> — Core's answer, not a second
/// implementation of it here. That matters more than it looks: the app is
/// asking the same question of the same file at the same time, and two
/// almost-identical schedule implementations would eventually disagree by a
/// minute and start a queue twice.
/// </summary>
internal sealed class SchedulerLoop
{
    /// <summary>
    /// How often the clock is checked. Schedules have minute resolution, so this
    /// only needs to be under a minute; it also bounds how long the agent takes
    /// to notice a queue's schedule was switched off, and how late a run can be
    /// after the machine wakes from sleep.
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    private readonly IQueueRepository _repository;

    /// <summary>
    /// The window start each queue was last launched for. Belt to
    /// <see cref="DownloadQueue.LastRunAt"/>'s braces: the app records the run in
    /// queues.json, but only once it has actually started, and this stops the
    /// agent from launching a second time in the seconds before that lands — or
    /// at all, if the launch failed to take.
    /// </summary>
    private readonly Dictionary<Guid, DateTime> _launched = new();

    private bool _loggedUnreadable;

    public SchedulerLoop(IQueueRepository repository) => _repository = repository;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        AgentLog.Write("scheduler started");

        while (!cancellationToken.IsCancellationRequested)
        {
            if (EvaluateOnce() == TickOutcome.NothingToWatch)
            {
                AgentLog.Write("no queue has a schedule — scheduler exiting");
                return;
            }

            await Task.Delay(TickInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One pass over the queue list. Public so <c>--once</c> can run exactly one.</summary>
    public TickOutcome EvaluateOnce()
    {
        if (!_repository.TryLoadAll(out var queues))
        {
            // Unreadable is not the same as empty, and must never be treated as
            // "nothing to watch": the file is being written by the app for a
            // fraction of a second at a time, and exiting on that would leave the
            // user with a scheduler that quietly disappeared.
            if (!_loggedUnreadable)
            {
                AgentLog.Write("queues.json could not be read — retrying");
                _loggedUnreadable = true;
            }
            return TickOutcome.Waiting;
        }

        _loggedUnreadable = false;

        var scheduled = queues.Where(queue => queue.Schedule.Enabled).ToList();
        if (scheduled.Count == 0) return TickOutcome.NothingToWatch;

        // Forget queues that are no longer scheduled, so the guard cannot grow
        // without bound over a long-running session.
        foreach (var staleId in _launched.Keys.Where(id => scheduled.All(queue => queue.Id != id)).ToList())
            _launched.Remove(staleId);

        var now = DateTime.Now;
        foreach (var queue in scheduled)
        {
            if (!queue.IsDue(now)) continue;

            DateTime windowStart = queue.Schedule.WindowStart(now)!.Value;
            if (_launched.TryGetValue(queue.Id, out var alreadyLaunched) && alreadyLaunched == windowStart)
                continue;

            if (QuickByteApp.IsRunning)
            {
                // QuickByte is up, and it runs the same schedule check itself.
                // Launching a second process would only be handed straight back
                // to that one, so the agent stands down and lets it happen there.
                AgentLog.Write($"'{queue.Name}' is due at {windowStart:g} — QuickByte is running and will start it");
                continue;
            }

            if (QuickByteApp.TryLaunchForQueue(queue.Id, out string? error))
            {
                _launched[queue.Id] = windowStart;
                AgentLog.Write($"started QuickByte for queue '{queue.Name}' (scheduled {windowStart:g})");
            }
            else
            {
                AgentLog.Write($"could not start QuickByte for queue '{queue.Name}': {error}");
            }
        }

        return TickOutcome.Waiting;
    }

    internal enum TickOutcome
    {
        /// <summary>There are scheduled queues; keep watching.</summary>
        Waiting,

        /// <summary>The queue list was read and no queue is scheduled — the agent has no reason to exist.</summary>
        NothingToWatch
    }
}

/// <summary>
/// Finding and starting the download manager itself. Both halves deliberately
/// avoid the registry: the agent is installed beside QuickByte.exe and can
/// simply look next to itself, and a scheduler that reads a path out of the Run
/// key would start whatever that key happened to point at.
/// </summary>
internal static class QuickByteApp
{
    private const string ExecutableName = "QuickByte.exe";

    /// <summary>
    /// The mutex QuickByte's <c>SingleInstance</c> holds for as long as it runs.
    /// Opening it is the cheapest possible "is the app up?", and it is the same
    /// name a second launch would collide with.
    /// </summary>
    private const string AppMutexName = @"Local\QuickByte.SingleInstance";

    public static bool IsRunning
    {
        get
        {
            try
            {
                using var mutex = Mutex.OpenExisting(AppMutexName);
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch
            {
                // Anything else (an access denial from a stricter session) is not
                // worth guessing about: treat the app as absent and launch it.
                // A launch that turns out to be redundant hands itself over and
                // exits, which is exactly what a second launch is for.
                return false;
            }
        }
    }

    /// <summary>
    /// Starts QuickByte on the given queue. <c>--minimized</c> is not optional
    /// here: this launch was asked for by a clock, not by a person, and a window
    /// appearing over whatever the user is doing at 03:00 — or at sign-in — is
    /// not what a schedule promised.
    /// </summary>
    public static bool TryLaunchForQueue(Guid queueId, out string? error)
    {
        error = null;

        string executable = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        if (!File.Exists(executable))
        {
            error = $"{executable} does not exist";
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("--run-queue");
            startInfo.ArgumentList.Add(queueId.ToString("D"));
            startInfo.ArgumentList.Add("--minimized");

            using var process = Process.Start(startInfo);
            return process is not null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
