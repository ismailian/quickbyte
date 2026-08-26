namespace QuickByte.Agent.Tests;

/// <summary>
/// The agent's entire command line: <c>--once</c>, which runs a single
/// evaluation and exits — what a person runs by hand to find out why last
/// night's queue did not start.
///
/// It is worth a test because of where the arguments come from. This process is
/// started by a Run-key value, which is a command line the shell has already had
/// its way with, and by a developer typing it at a prompt; a switch that is only
/// recognised in one of its spellings turns "why did nothing happen?" into a
/// second question on top of the first.
/// </summary>
public sealed class CommandLineTests
{
    [Theory]
    [InlineData("--once")]
    [InlineData("--ONCE")]
    [InlineData("--Once")]
    [InlineData("\"--once\"")]
    [InlineData("  --once  ")]
    public void The_once_switch_is_recognised_however_it_arrives(string argument) =>
        Assert.True(Program.HasSwitch(new[] { argument }, "--once"));

    [Theory]
    [InlineData("once")]
    [InlineData("-once")]
    [InlineData("--once-more")]
    [InlineData("")]
    public void Something_that_is_not_the_switch_is_not_the_switch(string argument) =>
        Assert.False(Program.HasSwitch(new[] { argument }, "--once"));

    [Fact]
    public void A_bare_launch_asks_for_the_service_not_a_single_pass() =>
        Assert.False(Program.HasSwitch(Array.Empty<string>(), "--once"));

    [Fact]
    public void The_switch_is_found_wherever_it_sits_on_the_line() =>
        Assert.True(Program.HasSwitch(new[] { @"C:\Program Files\QuickByte\QuickByte.Agent.exe", "--once" }, "--once"));
}
