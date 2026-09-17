using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using QuickByte.Core.Enums;
using QuickByte.Core.Helpers;
using QuickByte.Core.Models;

namespace QuickByte.UI;

/// <summary>
/// Wakes a sleeping computer for a scheduled queue that asks for it, with one
/// Task Scheduler task per user whose triggers are the queues' start times and
/// whose <c>WakeToRun</c> is what sets the wake timer.
///
/// The scheduler agent (<see cref="QueueAgentRegistration"/>) covers QuickByte
/// being closed; it cannot cover the machine being asleep, because nothing runs
/// then. A wake timer is the one mechanism Windows offers for that, and Task
/// Scheduler is the one place a program without administrator rights can set a
/// persistent one — checked, not assumed: a standard user can register a task
/// with <c>WakeToRun</c> in the root folder, for themselves.
///
/// The task runs as the signed-in user, only while they are signed in
/// (<c>InteractiveToken</c>), with least privilege: a download queue means
/// nothing outside the user's session. Its action is the agent with
/// <c>--once</c>, which starts whatever is due and exits; QuickByte then keeps
/// the machine awake while it downloads (<see cref="Controls.KeepAwake"/>).
///
/// Registered through <c>schtasks.exe</c> by full path, with an XML definition
/// built here — no COM, no P/Invoke. Best-effort and silent, like both Run-key
/// registrars: policy can refuse it, and the schedule still runs whenever the
/// machine happens to be awake.
/// </summary>
internal static class QueueWakeRegistration
{
    private const string TaskNamespace = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>A Monday, long past: triggers repeat weekly from it, and a fixed date keeps the definition comparable.</summary>
    private static readonly DateTime Anchor = new(2020, 1, 6);

    /// <summary>One per user — task names are machine-wide, and one user cannot replace another's task.</summary>
    internal static string TaskName => $"QuickByte Queue Wake ({Environment.UserName})";

    /// <summary>
    /// Runs schtasks.exe with the given arguments and returns its exit code. Settable
    /// for the tests, which must not register real wake timers on the machine.
    /// </summary>
    internal static Func<IReadOnlyList<string>, int> RunSchtasks { get; set; } = RunSchtasksProcess;

    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>The definition registered last in this session; "" once deleted, null before the first sync.</summary>
    private static string? _applied;

    /// <summary>
    /// Brings the task in line with the queues, off the UI thread: registered with
    /// their wake times when any queue wants waking, deleted when none does, and
    /// left alone when nothing changed since the last sync.
    /// </summary>
    public static Task SyncAsync(IEnumerable<DownloadQueue> queues)
    {
        var wakes = WakeTimes.For(queues.ToList());
        return Task.Run(async () =>
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Apply(wakes);
            }
            catch
            {
                // Best-effort — see the class comment.
            }
            finally
            {
                Gate.Release();
            }
        });
    }

    /// <summary>For the tests, and for a caller that is already off the UI thread.</summary>
    internal static void Apply(IReadOnlyList<WakeTime> wakes)
    {
        if (wakes.Count == 0 || ActionFor() is not { } action)
        {
            if (_applied == string.Empty) return;

            // Deleting a task that is not there fails; that is the state wanted.
            RunSchtasks(new[] { "/Delete", "/TN", TaskName, "/F" });
            _applied = string.Empty;
            return;
        }

        string xml = BuildDefinition(wakes, action.Command, action.Arguments, CurrentUserSid());
        if (xml == _applied) return;

        string path = Path.Combine(Path.GetTempPath(), $"quickbyte-wake-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks reads the definition as UTF-16, which is what the XML declares.
            File.WriteAllText(path, xml, Encoding.Unicode);
            if (RunSchtasks(new[] { "/Create", "/TN", TaskName, "/XML", path, "/F" }) == 0)
                _applied = xml;
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    /// <summary>Forgets what was registered, so the next sync writes the task again. For the tests.</summary>
    internal static void ResetForTest() => _applied = null;

    /// <summary>
    /// What the task starts: the scheduler agent for one evaluation when it ships
    /// beside the app, else QuickByte itself, minimized, whose own timer starts the
    /// queue. Null when neither can be found — a wake with nothing to run is not set.
    /// </summary>
    internal static (string Command, string Arguments)? ActionFor()
    {
        if (QueueAgentRegistration.ExecutablePath is { } agent) return (agent, "--once");
        if (Environment.ProcessPath is { } app && File.Exists(app)) return (app, "--minimized");
        return null;
    }

    internal static string BuildDefinition(IReadOnlyList<WakeTime> wakes, string command, string arguments, string userSid)
    {
        XNamespace ns = TaskNamespace;

        var triggers = new XElement(ns + "Triggers",
            wakes.Take(WakeTimes.MaxTriggers).Select(wake => new XElement(ns + "CalendarTrigger",
                new XElement(ns + "StartBoundary", (Anchor + wake.At).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)),
                new XElement(ns + "Enabled", "true"),
                new XElement(ns + "ScheduleByWeek",
                    new XElement(ns + "DaysOfWeek", DaysOf(wake.Days).Select(day => new XElement(ns + day.ToString()))),
                    new XElement(ns + "WeeksInterval", "1")))));

        var task = new XElement(ns + "Task",
            new XAttribute("version", "1.2"),
            new XElement(ns + "RegistrationInfo",
                new XElement(ns + "Author", "QuickByte"),
                new XElement(ns + "Description",
                    "Wakes the computer when a QuickByte download queue is scheduled to start. "
                    + "Managed by QuickByte: untick \"Wake the computer\" on the queue to remove it.")),
            triggers,
            new XElement(ns + "Principals",
                new XElement(ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(ns + "UserId", userSid),
                    new XElement(ns + "LogonType", "InteractiveToken"),
                    new XElement(ns + "RunLevel", "LeastPrivilege"))),
            new XElement(ns + "Settings",
                new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                new XElement(ns + "StopIfGoingOnBatteries", "false"),
                new XElement(ns + "AllowHardTerminate", "true"),
                new XElement(ns + "StartWhenAvailable", "false"),
                new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                new XElement(ns + "AllowStartOnDemand", "true"),
                new XElement(ns + "Enabled", "true"),
                new XElement(ns + "Hidden", "false"),
                new XElement(ns + "RunOnlyIfIdle", "false"),
                new XElement(ns + "WakeToRun", "true"),
                new XElement(ns + "ExecutionTimeLimit", "PT5M"),
                new XElement(ns + "Priority", "7")),
            new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(ns + "Exec",
                    new XElement(ns + "Command", command),
                    new XElement(ns + "Arguments", arguments),
                    new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(command) ?? string.Empty))));

        return new XDeclaration("1.0", "UTF-16", null) + Environment.NewLine + task;
    }

    private static IEnumerable<DayOfWeek> DaysOf(ScheduleDays days) =>
        new[] { DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday }
            .Where(day => (days & QueueSchedule.ToFlag(day)) != 0);

    private static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? identity.Name;
    }

    private static int RunSchtasksProcess(IReadOnlyList<string> arguments)
    {
        // By full path, never by name: a schtasks.exe earlier on PATH or in the
        // working folder would be handed the task definition instead.
        string schtasks = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
        var start = new ProcessStartInfo(schtasks)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start);
        if (process is null) return -1;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        return process.WaitForExit(30_000) ? process.ExitCode : -1;
    }
}
