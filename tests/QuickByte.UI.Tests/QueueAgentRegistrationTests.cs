namespace QuickByte.UI.Tests;

/// <summary>
/// The scheduler agent's own autostart entry, and how the app finds the agent.
///
/// Everything here is a statement about another executable: where it is (beside
/// this one, never a path read out of the registry), what it is called, and the
/// mutex it holds while it runs. Each of those is written down a second time in
/// <c>QuickByte.Agent</c>, and a disagreement between the two copies is a
/// scheduler that is registered but never runs, or one the app starts a second
/// copy of.
///
/// <see cref="QueueAgentRegistration.Sync"/> itself is deliberately not called:
/// its other half starts a process.
/// </summary>
public sealed class QueueAgentRegistrationTests : IDisposable
{
    private readonly ScratchRunKey _key = new();

    public void Dispose() => _key.Dispose();

    [Fact]
    public void The_agent_is_looked_for_beside_the_running_executable()
    {
        // Not from the Run key, which would start whatever that key happened to
        // point at, and not from an install path -- a copy run from somewhere
        // else has to use the agent that shipped with it.
        using var agent = AgentBesideTheHost.Present();

        string folder = Path.GetDirectoryName(Environment.ProcessPath!)!;

        Assert.Equal(Path.Combine(folder, QueueAgentRegistration.AgentFileName), QueueAgentRegistration.ExecutablePath);
        Assert.True(QueueAgentRegistration.IsAvailable);
    }

    [Fact]
    public void The_agent_that_shipped_with_this_copy_is_the_one_registered()
    {
        using var agent = AgentBesideTheHost.Present();

        QueueAgentRegistration.Register(true);

        Assert.Equal($"\"{QueueAgentRegistration.ExecutablePath}\"", _key.Value(QueueAgentRegistration.ValueName));
    }

    [Fact]
    public void No_agent_beside_the_app_means_no_entry_that_would_start_one()
    {
        // A build or an install that carries no agent must not leave a Run entry
        // pointing at a file that is not there. Scheduling still works while the
        // window is open, so this is a reason to say less, not to fail.
        using var agent = AgentBesideTheHost.Absent();

        Assert.Null(QueueAgentRegistration.ExecutablePath);
        Assert.False(QueueAgentRegistration.IsAvailable);

        QueueAgentRegistration.Register(true);

        Assert.Null(_key.Value(QueueAgentRegistration.ValueName));
    }

    [Fact]
    public void A_registered_path_is_quoted()
    {
        // Same command-line rule as StartupRegistration: an unquoted
        // C:\Program Files\... path runs C:\Program.exe, silently.
        using var agent = AgentBesideTheHost.Present();
        _key.SetValue(QueueAgentRegistration.ValueName, @"C:\Program Files\QuickByte\QuickByte.Agent.exe");

        QueueAgentRegistration.Register(true);

        string value = _key.Value(QueueAgentRegistration.ValueName)!;

        Assert.StartsWith("\"", value);
        Assert.EndsWith("\"", value);
        Assert.Equal(QueueAgentRegistration.ExecutablePath, value.Trim('"'));
    }

    [Fact]
    public void Deregistering_removes_whatever_was_there()
    {
        // The user unscheduled their last queue: the agent has no reason to
        // start again, and it exits on its own once it reads the same file.
        _key.SetValue(QueueAgentRegistration.ValueName, @"""C:\somewhere\QuickByte.Agent.exe""");

        QueueAgentRegistration.Register(false);

        Assert.Null(_key.Value(QueueAgentRegistration.ValueName));
    }

    [Fact]
    public void Deregistering_when_nothing_is_registered_is_harmless()
    {
        QueueAgentRegistration.Register(false);

        Assert.Empty(_key.ValueNames());
    }

    [Fact]
    public void The_apps_entry_is_not_touched_by_the_agents()
    {
        // Both live under the same key, and the user may well have neither, one
        // or both. Deleting one must not disturb the other.
        StartupRegistration.TryApply(true, out _);

        QueueAgentRegistration.Register(false);

        Assert.True(StartupRegistration.IsEnabled);
    }

    [Fact]
    public void The_agent_is_named_and_watched_for_exactly_as_the_agent_names_itself()
    {
        // Written down again in QuickByte.Agent: AgentFileName is what the build
        // copies beside QuickByte.exe, and the mutex is the one the agent holds
        // for its whole life (its Program.cs). Change one copy only and the app
        // either cannot find the agent or starts a second one over it.
        Assert.Equal("QuickByte.Agent.exe", QueueAgentRegistration.AgentFileName);
        Assert.Equal(@"Local\QuickByte.QueueAgent", QueueAgentRegistration.AgentMutexName);
    }

    [Fact]
    public void The_scheduler_has_its_own_row_in_Task_Manager()
    {
        // A separate value from the app's, so a user can see -- and stop -- the
        // background scheduler without touching QuickByte's own autostart.
        Assert.Equal("QuickByteScheduler", QueueAgentRegistration.ValueName);
        Assert.NotEqual(StartupRegistration.ValueName, QueueAgentRegistration.ValueName);
    }
}
