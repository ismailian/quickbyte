using System.Threading;

namespace QuickByte.Agent.Tests;

/// <summary>
/// The two halves of "start the download manager": finding out whether it is
/// already up, and building the launch.
///
/// Both are agreements with a process that is not here — the mutex name and the
/// command line are QuickByte's, not the agent's — which is exactly why they are
/// worth pinning. Nothing here starts anything: the command line is inspected
/// where it is built.
/// </summary>
public sealed class QuickByteAppTests
{
    // ------------------------------------------------- is the app up? --

    [Fact]
    public void A_named_mutex_is_present_only_while_something_holds_it()
    {
        // A name of this test's own, so the answer does not depend on whether
        // QuickByte happens to be running on the machine running the tests.
        string name = @"Local\QuickByte.Agent.Tests." + Guid.NewGuid().ToString("N");

        Assert.False(QuickByteApp.IsMutexPresent(name));

        using (var mutex = new Mutex(initiallyOwned: false, name, out bool createdNew))
        {
            Assert.True(createdNew);
            Assert.True(QuickByteApp.IsMutexPresent(name));
        }

        Assert.False(QuickByteApp.IsMutexPresent(name));
    }

    [Fact]
    public void A_malformed_mutex_name_reads_as_not_running_rather_than_throwing()
    {
        // Everything but "it isn't there" is swallowed on purpose: a launch that
        // turns out to be redundant hands itself over and exits, which is a far
        // better outcome than an agent that dies on a probe.
        Assert.False(QuickByteApp.IsMutexPresent(new string('x', 1024)));
    }

    [Fact]
    public void The_app_is_looked_for_under_the_name_it_holds_itself()
    {
        // UI/SingleInstance.cs creates this mutex for as long as QuickByte runs,
        // and UI/QueueAgentRegistration.cs watches for the agent's own the same
        // way. Change one of the three and the agent starts a second QuickByte
        // over a running one, which is the failure this string prevents.
        Assert.Equal(@"Local\QuickByte.SingleInstance", QuickByteApp.AppMutexName);
    }

    // --------------------------------------------------- starting it --

    [Fact]
    public void QuickByte_is_looked_for_beside_the_agent_and_its_absence_reported()
    {
        // The agent ships in QuickByte's own folder, so it looks next to itself
        // rather than at a path out of the Run key. Here there is nothing beside
        // it, which is the "run from a build that has no app" case: it has to
        // report that, not throw out of the tick.
        string beside = Path.Combine(AppContext.BaseDirectory, QuickByteApp.ExecutableName);
        Assert.False(File.Exists(beside), $"{beside} exists, so this test cannot say anything");

        bool launched = new QuickByteApp().TryLaunchForQueue(Guid.NewGuid(), out string? error);

        Assert.False(launched);
        Assert.Contains(QuickByteApp.ExecutableName, error);
    }

    [Fact]
    public void The_launch_carries_the_queue_and_the_switches_QuickByte_reads()
    {
        var queueId = Guid.NewGuid();

        var startInfo = QuickByteApp.BuildStartInfo(@"C:\Program Files\QuickByte\QuickByte.exe", queueId);

        // --run-queue {guid} is what UI/SingleInstance.FindQueueId parses, and
        // --minimized is not optional: a clock asked for this launch, not a
        // person, and a window has no business appearing at 03:00.
        Assert.Equal(new[] { "--run-queue", queueId.ToString("D"), "--minimized" }, startInfo.ArgumentList);
        Assert.Equal(queueId, Guid.Parse(startInfo.ArgumentList[1]));
    }

    [Fact]
    public void The_launch_is_a_plain_process_start_with_its_arguments_kept_apart()
    {
        var startInfo = QuickByteApp.BuildStartInfo(@"C:\Program Files\QuickByte\QuickByte.exe", Guid.NewGuid());

        // UseShellExecute false because this is an executable beside the agent,
        // not a document; ArgumentList rather than Arguments because the install
        // path has a space in it and quoting it by hand is how that gets broken.
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(string.Empty, startInfo.Arguments);
        Assert.Equal(@"C:\Program Files\QuickByte\QuickByte.exe", startInfo.FileName);
        Assert.Equal(AppContext.BaseDirectory, startInfo.WorkingDirectory);
    }
}
