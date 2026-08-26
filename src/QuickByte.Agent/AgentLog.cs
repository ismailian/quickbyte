namespace QuickByte.Agent;

/// <summary>
/// A one-line-at-a-time log next to the settings the agent reads
/// (%AppData%/QuickByte/agent.log).
///
/// A background process with no window and no console has no other way to
/// explain itself, and "my queue did not start last night" is a question that
/// can only be answered after the fact. It is capped and rewritten rather than
/// rotated: this is a diagnostic aid, not a record, and a scheduler that
/// silently fills a user's profile with logs would be a worse bug than the one
/// it is there to help find.
/// </summary>
internal static class AgentLog
{
    private const long MaxBytes = 64 * 1024;

    private static readonly object Sync = new();

    /// <summary>
    /// Where the line goes. Settable only so the tests can point it at a scratch
    /// file: this log is the one place the agent's decisions are observable, and
    /// a test run has no business appending to — or, past the cap, deleting —
    /// the user's real agent.log.
    /// </summary>
    internal static string LogPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuickByte", "agent.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                string path = LogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                    File.Delete(path);

                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Best-effort by definition: a log that can throw is a scheduler that
            // can miss a run because its disk was full.
        }
    }
}
