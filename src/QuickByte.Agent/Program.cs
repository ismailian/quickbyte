using System.Threading;
using QuickByte.Core.Services;

namespace QuickByte.Agent;

/// <summary>
/// QuickByte's scheduler agent: a tiny, windowless process whose only job is to
/// be there when QuickByte is not.
///
/// A queue that starts at 03:00 is only useful if something is watching the
/// clock at 03:00, and the download manager itself may well have been closed
/// hours earlier — a schedule that silently requires the app to be left running
/// is not a schedule. So the agent runs from the user's sign-in, reads the same
/// queues.json QuickByte writes, and when a queue comes due it launches
/// QuickByte and hands it the queue to start.
///
/// It deliberately does not download anything, does not have a window, and holds
/// no state of its own: everything it knows is in queues.json, so the app
/// remains the only writer and the agent can be killed, restarted or updated
/// without anything being lost. It exits on its own the moment no queue has a
/// schedule left, so a user who never schedules anything never has a background
/// process.
/// </summary>
internal static class Program
{
    /// <summary>
    /// One agent per signed-in user — "Local\" scopes it to the logon session,
    /// matching QuickByte's own single-instance mutex. Two agents would launch
    /// the app twice for one schedule.
    /// </summary>
    private const string MutexName = @"Local\QuickByte.QueueAgent";

    private static int Main(string[] args)
    {
        using var mutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
        if (!createdNew) return 0;

        using var cancellation = new CancellationTokenSource();

        // Sign-out and shutdown arrive as a process exit rather than a signal for
        // a windowless process; this is what still gives the loop a chance to
        // write its last log line.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cancellation.Cancel();

        var loop = new SchedulerLoop(new QueueRepository());

        if (HasSwitch(args, "--once"))
        {
            // One evaluation and out: what a person runs by hand to find out why
            // last night's queue did not start. Everything it decides goes to the
            // same log the service writes.
            loop.EvaluateOnce();
            return 0;
        }

        try
        {
            loop.RunAsync(cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Signed out mid-wait.
        }
        catch (Exception ex)
        {
            AgentLog.Write($"agent stopped unexpectedly: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static bool HasSwitch(IEnumerable<string> args, string name) =>
        args.Any(argument => string.Equals(argument.Trim().Trim('"'), name, StringComparison.OrdinalIgnoreCase));
}
