namespace QuickByte.UI.Tests;

/// <summary>
/// Puts <c>QuickByte.Agent.exe</c> beside the running test host, or takes it
/// away, so both halves of "is there an agent to register?" can be reached.
///
/// <see cref="QueueAgentRegistration"/> resolves the agent from the running
/// executable's own folder — which, under a test run, is this project's output
/// folder. Whether the build happens to have copied the agent there decides
/// which branch runs, and a test whose meaning depends on that is a test that
/// quietly stops checking anything. Both directions put the folder back as they
/// found it.
/// </summary>
internal sealed class AgentBesideTheHost : IDisposable
{
    private readonly string _path;
    private readonly string _movedTo;
    private readonly bool _created;
    private readonly bool _moved;

    private AgentBesideTheHost(bool present)
    {
        string folder = Path.GetDirectoryName(Environment.ProcessPath!)!;
        _path = Path.Combine(folder, QueueAgentRegistration.AgentFileName);
        _movedTo = _path + ".hidden-for-test";

        if (present && !File.Exists(_path))
        {
            File.WriteAllBytes(_path, Array.Empty<byte>());
            _created = true;
        }
        else if (!present && File.Exists(_path))
        {
            File.Move(_path, _movedTo, overwrite: true);
            _moved = true;
        }
    }

    /// <summary>An installed layout: the app and its scheduler side by side.</summary>
    public static AgentBesideTheHost Present() => new(present: true);

    /// <summary>A build or an install that carries no agent at all.</summary>
    public static AgentBesideTheHost Absent() => new(present: false);

    public void Dispose()
    {
        try
        {
            if (_created) File.Delete(_path);
            else if (_moved) File.Move(_movedTo, _path, overwrite: true);
        }
        catch { /* best-effort */ }
    }
}
