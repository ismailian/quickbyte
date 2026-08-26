namespace QuickByte.Agent.Tests;

/// <summary>
/// Redirects <see cref="AgentLog"/> to a scratch file for the length of a test,
/// and reads it back.
///
/// The agent has no window, no console and no return value: what it decided is
/// only ever visible in agent.log, so several of these tests are assertions
/// about lines. Pointing the log somewhere else is not only about keeping the
/// user's real agent.log clean — the log is capped and *deleted* when it grows
/// past the cap, which is not something a test run should be doing to a
/// diagnostic file someone may be about to read.
/// </summary>
internal sealed class LogFile : IDisposable
{
    private readonly string _previous;

    public LogFile()
    {
        _previous = AgentLog.LogPath;

        Folder = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "QuickByte.Agent.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);

        AgentLog.LogPath = System.IO.Path.Combine(Folder, "agent.log");
    }

    /// <summary>The scratch directory, for a test that needs a second file beside the log.</summary>
    public string Folder { get; }

    public string Path => AgentLog.LogPath;

    public string Text => File.Exists(Path) ? File.ReadAllText(Path) : string.Empty;

    public IReadOnlyList<string> Lines =>
        Text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>How many lines mention <paramref name="fragment"/>.</summary>
    public int Count(string fragment) =>
        Lines.Count(line => line.Contains(fragment, StringComparison.Ordinal));

    public void Dispose()
    {
        AgentLog.LogPath = _previous;
        try { if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true); }
        catch { /* best-effort, matching the agent's own cleanup idiom */ }
    }
}
