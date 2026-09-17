using System.Text;
using System.Xml.Linq;
using QuickByte.Core.Enums;
using QuickByte.Core.Helpers;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Tests;

/// <summary>
/// The wake timer's task: what it is registered with, and when schtasks.exe is
/// asked to change it. schtasks itself is replaced for every test — a test run
/// that registered real wake timers would switch the machine on in the night.
/// </summary>
public sealed class QueueWakeRegistrationTests : IDisposable
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private readonly Func<IReadOnlyList<string>, int> _realRunner = QueueWakeRegistration.RunSchtasks;
    private readonly List<(string[] Arguments, string? Definition)> _calls = new();
    private int _exitCode;

    public QueueWakeRegistrationTests()
    {
        QueueWakeRegistration.ResetForTest();
        QueueWakeRegistration.RunSchtasks = arguments =>
        {
            string[] copy = arguments.ToArray();
            int xml = Array.IndexOf(copy, "/XML");
            _calls.Add((copy, xml >= 0 && File.Exists(copy[xml + 1]) ? File.ReadAllText(copy[xml + 1]) : null));
            return _exitCode;
        };
    }

    public void Dispose()
    {
        QueueWakeRegistration.RunSchtasks = _realRunner;
        QueueWakeRegistration.ResetForTest();
    }

    private static readonly WakeTime[] TwoWakes =
    {
        new(ScheduleDays.Weekdays, new TimeSpan(2, 30, 0)),
        new(ScheduleDays.Sunday | ScheduleDays.Saturday, new TimeSpan(23, 5, 9))
    };

    [Fact]
    public void The_task_wakes_the_computer_as_the_signed_in_user_with_least_privilege()
    {
        var task = XDocument.Parse(QueueWakeRegistration.BuildDefinition(TwoWakes, @"C:\Apps\QuickByte.Agent.exe", "--once", "S-1-5-21-1-2-3-1001")).Root!;

        Assert.Equal("true", task.Descendants(Ns + "WakeToRun").Single().Value);
        Assert.Equal("S-1-5-21-1-2-3-1001", task.Descendants(Ns + "UserId").Single().Value);
        Assert.Equal("InteractiveToken", task.Descendants(Ns + "LogonType").Single().Value);
        Assert.Equal("LeastPrivilege", task.Descendants(Ns + "RunLevel").Single().Value);

        // A wake on battery is still a wake the user asked for.
        Assert.Equal("false", task.Descendants(Ns + "DisallowStartIfOnBatteries").Single().Value);

        var exec = task.Descendants(Ns + "Exec").Single();
        Assert.Equal(@"C:\Apps\QuickByte.Agent.exe", exec.Element(Ns + "Command")!.Value);
        Assert.Equal("--once", exec.Element(Ns + "Arguments")!.Value);
        Assert.Equal(@"C:\Apps", exec.Element(Ns + "WorkingDirectory")!.Value);
    }

    [Fact]
    public void Each_wake_is_a_weekly_trigger_on_its_days_at_its_time()
    {
        var task = XDocument.Parse(QueueWakeRegistration.BuildDefinition(TwoWakes, @"C:\Apps\QuickByte.Agent.exe", "--once", "S-1")).Root!;
        var triggers = task.Descendants(Ns + "CalendarTrigger").ToList();

        Assert.Equal(2, triggers.Count);
        Assert.Equal("2020-01-06T02:30:00", triggers[0].Element(Ns + "StartBoundary")!.Value);
        Assert.Equal("2020-01-06T23:05:09", triggers[1].Element(Ns + "StartBoundary")!.Value);
        Assert.Equal(
            new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" },
            triggers[0].Descendants(Ns + "DaysOfWeek").Elements().Select(e => e.Name.LocalName));
        Assert.Equal(
            new[] { "Sunday", "Saturday" },
            triggers[1].Descendants(Ns + "DaysOfWeek").Elements().Select(e => e.Name.LocalName));
        Assert.All(triggers, t => Assert.Equal("1", t.Descendants(Ns + "WeeksInterval").Single().Value));
    }

    [Fact]
    public void A_path_with_characters_that_mean_something_in_xml_stays_the_path()
    {
        string odd = @"C:\Tools & <Things>\QuickByte.Agent.exe";

        var task = XDocument.Parse(QueueWakeRegistration.BuildDefinition(TwoWakes, odd, "--once", "S-1")).Root!;

        Assert.Equal(odd, task.Descendants(Ns + "Command").Single().Value);
    }

    [Fact]
    public void Wake_times_register_the_task_once_as_utf16_and_leave_no_file_behind()
    {
        QueueWakeRegistration.Apply(TwoWakes);
        QueueWakeRegistration.Apply(TwoWakes);

        var (arguments, definition) = Assert.Single(_calls);
        Assert.Equal(new[] { "/Create", "/TN", QueueWakeRegistration.TaskName, "/XML" }, arguments[..4]);
        Assert.Equal("/F", arguments[^1]);
        Assert.Contains("<WakeToRun>true</WakeToRun>", definition);
        Assert.False(File.Exists(arguments[4]));
    }

    [Fact]
    public void The_definition_is_written_as_the_utf16_its_declaration_names()
    {
        byte[]? bytes = null;
        QueueWakeRegistration.RunSchtasks = arguments =>
        {
            bytes = File.ReadAllBytes(arguments[Array.IndexOf(arguments.ToArray(), "/XML") + 1]);
            return 0;
        };

        QueueWakeRegistration.Apply(TwoWakes);

        Assert.Equal(new byte[] { 0xFF, 0xFE }, bytes![..2]);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2));
    }

    [Fact]
    public void A_changed_time_registers_again_and_a_refused_registration_is_tried_again()
    {
        _exitCode = 1;
        QueueWakeRegistration.Apply(TwoWakes);
        _exitCode = 0;
        QueueWakeRegistration.Apply(TwoWakes);
        QueueWakeRegistration.Apply(new[] { new WakeTime(ScheduleDays.Monday, TimeSpan.FromHours(4)) });

        Assert.Equal(3, _calls.Count);
        Assert.All(_calls, call => Assert.Equal("/Create", call.Arguments[0]));
    }

    [Fact]
    public void No_queue_wanting_a_wake_deletes_the_task_once()
    {
        QueueWakeRegistration.Apply(TwoWakes);
        QueueWakeRegistration.Apply(Array.Empty<WakeTime>());
        QueueWakeRegistration.Apply(Array.Empty<WakeTime>());

        Assert.Equal(2, _calls.Count);
        Assert.Equal(new[] { "/Delete", "/TN", QueueWakeRegistration.TaskName, "/F" }, _calls[1].Arguments);
    }

    [Fact]
    public void The_task_runs_the_agent_for_one_look_when_it_ships_beside_the_app()
    {
        using (AgentBesideTheHost.Present())
            Assert.Equal((QueueAgentRegistration.ExecutablePath!, "--once"), QueueWakeRegistration.ActionFor());

        // Without one, QuickByte itself — minimized, its own timer starts the queue.
        using (AgentBesideTheHost.Absent())
            Assert.Equal((Environment.ProcessPath!, "--minimized"), QueueWakeRegistration.ActionFor());
    }

    [Fact]
    public void The_task_is_named_for_the_user_so_two_accounts_never_share_one()
    {
        Assert.EndsWith($"({Environment.UserName})", QueueWakeRegistration.TaskName);
        Assert.DoesNotContain('\\', QueueWakeRegistration.TaskName);
    }
}

public sealed class KeepAwakeTests
{
    [Fact]
    public void A_request_is_held_only_while_downloads_run_and_released_on_dispose()
    {
        using var keepAwake = new KeepAwake();

        keepAwake.Update(true);
        Assert.True(keepAwake.IsHeld);

        keepAwake.Update(true);
        Assert.True(keepAwake.IsHeld);

        keepAwake.Update(false);
        Assert.False(keepAwake.IsHeld);

        keepAwake.Update(true);
        keepAwake.Dispose();
        Assert.False(keepAwake.IsHeld);
    }
}
