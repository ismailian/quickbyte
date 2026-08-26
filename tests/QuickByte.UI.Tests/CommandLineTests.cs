namespace QuickByte.UI.Tests;

/// <summary>
/// Everything QuickByte can be started with, and what it makes of it.
///
/// This is the only part of the UI project other processes talk to: the shell
/// hands it a link, the scheduler agent hands it <c>--run-queue {guid}</c>, and
/// a second launch hands whatever it was given down a pipe to the copy already
/// running. Each of those is a contract with something that is not here, and a
/// misread argument is silent — the app opens an empty window and the user is
/// left wondering why their download, or their queue, did nothing.
/// </summary>
public sealed class CommandLineTests
{
    private static readonly Guid QueueId = new("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    // ------------------------------------------------------------- URLs --

    [Theory]
    [InlineData("http://example.com/file.zip")]
    [InlineData("https://example.com/file.zip")]
    [InlineData("ftp://example.com/file.zip")]
    [InlineData("ftps://example.com/file.zip")]
    public void Every_scheme_the_Add_dialog_accepts_is_recognised_on_the_command_line(string url)
    {
        // FTP included: a link handed over by the shell has to reach the dialog
        // rather than being dropped as "not a URL".
        Assert.Equal(url, SingleInstance.FindUrl(new[] { url }));
    }

    [Fact]
    public void A_url_is_found_past_the_switches_around_it()
    {
        string[] arguments = { "--minimized", "https://example.com/file.zip", "--run-queue" };

        Assert.Equal("https://example.com/file.zip", SingleInstance.FindUrl(arguments));
    }

    [Fact]
    public void The_first_url_wins()
    {
        string[] arguments = { "https://first.example/a.zip", "https://second.example/b.zip" };

        Assert.Equal("https://first.example/a.zip", SingleInstance.FindUrl(arguments));
    }

    [Theory]
    [InlineData("\"https://example.com/file.zip\"")]
    [InlineData("  https://example.com/file.zip  ")]
    public void The_shell_may_quote_or_pad_a_link_and_it_is_still_a_link(string argument)
    {
        Assert.Equal("https://example.com/file.zip", SingleInstance.FindUrl(new[] { argument }));
    }

    [Theory]
    [InlineData("example.com/file.zip")]          // no scheme: not absolute
    [InlineData("--minimized")]
    [InlineData("")]
    public void Something_that_is_not_a_link_is_not_offered_as_one(string argument)
    {
        Assert.Null(SingleInstance.FindUrl(new[] { argument }));
    }

    [Fact]
    public void A_local_path_is_not_a_download()
    {
        // Uri parses this happily as file:// — the scheme list is what keeps a
        // double-clicked file from being treated as a URL to fetch.
        Assert.Null(SingleInstance.FindUrl(new[] { @"C:\Users\someone\Downloads\file.zip" }));
    }

    // ----------------------------------------------------------- queues --

    [Fact]
    public void The_agents_switch_form_is_understood()
    {
        Assert.Equal(QueueId, SingleInstance.FindQueueId(new[] { "--run-queue", QueueId.ToString("D") }));
    }

    [Fact]
    public void The_payload_form_that_crosses_the_pipe_is_understood()
    {
        Assert.Equal(QueueId, SingleInstance.FindQueueId(new[] { "run-queue:" + QueueId.ToString("D") }));
    }

    [Theory]
    [InlineData("--RUN-QUEUE")]
    [InlineData("--Run-Queue")]
    public void The_switch_is_matched_whatever_its_case(string spelling)
    {
        Assert.Equal(QueueId, SingleInstance.FindQueueId(new[] { spelling, QueueId.ToString("D") }));
    }

    [Fact]
    public void A_quoted_switch_and_id_still_name_a_queue()
    {
        Assert.Equal(QueueId, SingleInstance.FindQueueId(new[] { "\"--run-queue\"", "\"" + QueueId + "\"" }));
    }

    [Fact]
    public void A_switch_with_nothing_after_it_names_no_queue()
    {
        Assert.Null(SingleInstance.FindQueueId(new[] { "--run-queue" }));
    }

    [Fact]
    public void A_switch_followed_by_something_that_is_not_an_id_names_no_queue()
    {
        Assert.Null(SingleInstance.FindQueueId(new[] { "--run-queue", "nightly" }));
    }

    [Fact]
    public void An_ordinary_launch_names_no_queue()
    {
        Assert.Null(SingleInstance.FindQueueId(new[] { "https://example.com/file.zip", "--minimized" }));
    }

    // --------------------------------------------------------- hand-off --

    [Fact]
    public void A_queue_is_what_gets_handed_over_when_both_are_present()
    {
        // The agent's launch is the one that carries an instruction; a URL on
        // the same line would only open a dialog.
        string payload = SingleInstance.BuildHandoffPayload(
            new[] { "--run-queue", QueueId.ToString("D"), "https://example.com/file.zip" });

        Assert.Equal("run-queue:" + QueueId.ToString("D"), payload);
    }

    [Fact]
    public void What_is_handed_over_is_what_the_other_side_reads_back()
    {
        // The two halves of one contract: this string crosses a named pipe and
        // is parsed by the running instance. If they ever disagree, a scheduled
        // queue silently opens an empty window instead of starting.
        string payload = SingleInstance.BuildHandoffPayload(new[] { "--run-queue", QueueId.ToString("D") });

        Assert.Equal(QueueId, SingleInstance.FindQueueId(new[] { payload }));
    }

    [Fact]
    public void A_link_is_handed_over_when_there_is_no_queue()
    {
        Assert.Equal(
            "https://example.com/file.zip",
            SingleInstance.BuildHandoffPayload(new[] { "--minimized", "https://example.com/file.zip" }));
    }

    [Fact]
    public void A_launch_with_nothing_to_say_hands_over_nothing()
    {
        // Not null: the payload is written to a pipe, and the receiving side
        // reads "no argument" as an empty string.
        Assert.Equal(string.Empty, SingleInstance.BuildHandoffPayload(Array.Empty<string>()));
        Assert.Equal(string.Empty, SingleInstance.BuildHandoffPayload(new[] { "--minimized" }));
    }

    [Fact]
    public void The_queue_id_is_handed_over_in_the_form_it_is_parsed_in()
    {
        // "D" — plain hyphenated, no braces. Guid.TryParse would take either,
        // but the wire form is worth pinning: it is also what the agent writes.
        string payload = SingleInstance.BuildHandoffPayload(new[] { "--run-queue", QueueId.ToString("B") });

        Assert.Equal("run-queue:" + QueueId.ToString("D"), payload);
        Assert.DoesNotContain("{", payload);
    }

    // -------------------------------------------------- quiet launches --

    [Theory]
    [InlineData("--minimized")]
    [InlineData("-minimized")]
    [InlineData("/minimized")]
    [InlineData("-m")]
    [InlineData("\"--minimized\"")]
    [InlineData("  -m  ")]
    public void Every_spelling_of_the_minimized_switch_starts_quietly(string argument)
    {
        Assert.True(Program.HasMinimizedSwitch(new[] { argument }));
    }

    [Theory]
    [InlineData("--min")]
    [InlineData("m")]
    [InlineData("--minimize")]
    [InlineData("https://example.com/file.zip")]
    public void Anything_else_opens_a_window(string argument)
    {
        Assert.False(Program.HasMinimizedSwitch(new[] { argument }));
    }

    [Fact]
    public void The_minimized_switch_is_matched_case_sensitively()
    {
        // Unlike --run-queue, which FindQueueId matches case-insensitively.
        // Everything that writes this switch (the Run key, the agent, a
        // shortcut) writes it in lower case, so nothing is broken by it — but
        // the asymmetry is real, and a hand-typed --Minimized opens a window.
        Assert.False(Program.HasMinimizedSwitch(new[] { "--Minimized" }));
    }
}
