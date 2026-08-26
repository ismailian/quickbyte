using System.Globalization;

namespace QuickByte.Agent.Tests;

/// <summary>
/// The log is the agent's only voice — no window, no console, and a question
/// ("why did my queue not start last night?") that can only be answered after
/// the fact. So it has to write, it has to stay small, and above all it must
/// never throw: a scheduler that dies because a disk is full has failed at the
/// one job the log exists to explain.
/// </summary>
public sealed class AgentLogTests : IDisposable
{
    private readonly LogFile _log = new();

    public void Dispose() => _log.Dispose();

    [Fact]
    public void A_line_carries_the_time_it_happened()
    {
        AgentLog.Write("scheduler started");

        string line = Assert.Single(_log.Lines);
        Assert.EndsWith("scheduler started", line);

        // Parsed with the current culture because that is what wrote it — the
        // ':' in the format is the culture's time separator, not a literal. Same
        // trap as ByteFormatterTests in the Core suite.
        Assert.True(
            DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss",
                CultureInfo.CurrentCulture, DateTimeStyles.None, out _),
            $"no timestamp at the head of: {line}");
    }

    [Fact]
    public void Lines_accumulate_in_the_order_they_were_written()
    {
        AgentLog.Write("scheduler started");
        AgentLog.Write("started QuickByte for queue 'Nightly'");

        Assert.Equal(2, _log.Lines.Count);
        Assert.EndsWith("scheduler started", _log.Lines[0]);
        Assert.EndsWith("started QuickByte for queue 'Nightly'", _log.Lines[1]);
    }

    [Fact]
    public void A_log_that_is_still_under_its_cap_is_appended_to()
    {
        File.WriteAllText(_log.Path, new string('x', 60 * 1024) + Environment.NewLine);

        AgentLog.Write("last night");

        Assert.Contains("xxxx", _log.Text);
        Assert.EndsWith("last night", _log.Lines[^1]);
    }

    [Fact]
    public void A_log_past_its_cap_is_started_again_rather_than_rotated()
    {
        // Capped and rewritten on purpose: this is a diagnostic aid, not a
        // record, and a background process quietly filling a user's profile with
        // logs would be a worse bug than the one it is there to help find.
        File.WriteAllText(_log.Path, new string('x', 70 * 1024) + Environment.NewLine);

        AgentLog.Write("after the cap");

        Assert.DoesNotContain("xxxx", _log.Text);
        Assert.EndsWith("after the cap", Assert.Single(_log.Lines));
        Assert.Single(Directory.GetFiles(_log.Folder));
    }

    [Fact]
    public void A_log_that_cannot_be_written_is_not_a_crash()
    {
        // Its folder is a file, so even creating the directory fails. Every
        // caller in the agent logs on a path it is in the middle of deciding
        // something on; none of them can afford an exception from it.
        string blocker = Path.Combine(_log.Folder, "blocker");
        File.WriteAllText(blocker, "not a folder");
        AgentLog.LogPath = Path.Combine(blocker, "agent.log");

        AgentLog.Write("nowhere to go");

        Assert.Equal("not a folder", File.ReadAllText(blocker));
    }
}
