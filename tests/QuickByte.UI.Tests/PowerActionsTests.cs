using QuickByte.Core.Enums;
using QuickByte.UI;

namespace QuickByte.UI.Tests;

/// <summary>
/// The part of turning a computer off that can be checked without doing it: which
/// program is run, how, and that every choice has words for the countdown that
/// stands in front of it. Nothing here calls <see cref="PowerActions.Perform"/>.
/// </summary>
public sealed class PowerActionsTests
{
    [Fact]
    public void Shutdown_runs_the_system_copy_of_shutdown_exe_by_its_full_path()
    {
        // By name, it would be resolved against the working directory and PATH
        // first — a download manager's working directory is exactly where a file
        // called shutdown.exe could have been put by someone else.
        var command = PowerActions.ShutdownCommand();

        Assert.Equal(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), command.FileName);
        Assert.True(Path.IsPathFullyQualified(command.FileName));
        Assert.False(command.UseShellExecute);
    }

    [Fact]
    public void Shutdown_never_forces_apps_with_unsaved_work_closed()
    {
        var arguments = PowerActions.ShutdownCommand().Arguments.Split(' ');

        Assert.Contains("/s", arguments);
        Assert.DoesNotContain("/f", arguments);
        Assert.DoesNotContain("/p", arguments);
    }

    [Fact]
    public void Every_action_can_be_chosen_from_the_drop_downs()
    {
        Assert.Equal(Enum.GetValues<PowerAction>().Order(), PowerActions.All.Order());
    }

    [Fact]
    public void Every_action_has_its_own_words_for_the_countdown()
    {
        var actions = Enum.GetValues<PowerAction>().Where(a => a != PowerAction.None).ToList();

        Assert.Equal(actions.Count, actions.Select(PowerActions.Label).Distinct().Count());
        Assert.Equal(actions.Count, actions.Select(PowerActions.Verb).Distinct().Count());
        Assert.Equal(actions.Count, actions.Select(PowerActions.NowButton).Distinct().Count());
        Assert.DoesNotContain(actions, a => PowerActions.Label(a) == PowerActions.Label(PowerAction.None));
    }

    [Fact]
    public void Turning_the_computer_off_gives_longer_to_reach_the_keyboard_than_closing_the_app()
    {
        Assert.True(PowerActions.CountdownSeconds(PowerAction.ShutDown) >= 60);
        Assert.True(PowerActions.CountdownSeconds(PowerAction.Sleep) >= 60);
        Assert.True(PowerActions.CountdownSeconds(PowerAction.ExitApp) > 0);
        Assert.True(PowerActions.CountdownSeconds(PowerAction.ExitApp) < PowerActions.CountdownSeconds(PowerAction.ShutDown));
    }

    [Fact]
    public void An_action_it_does_not_know_is_not_performed()
    {
        Assert.False(PowerActions.Perform(PowerAction.None));
        Assert.False(PowerActions.Perform((PowerAction)42));
    }
}
