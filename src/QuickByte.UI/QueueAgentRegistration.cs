using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;

namespace QuickByte.UI;

/// <summary>
/// Keeps QuickByte's scheduler agent (<c>QuickByte.Agent.exe</c>) installed and
/// running for exactly as long as there is a schedule for it to watch.
///
/// The agent is the answer to the obvious hole in queue scheduling: a queue set
/// to start at 03:00 cannot start itself if the download manager was closed at
/// midnight. The agent is a separate process that outlives QuickByte, starts
/// with the user's session, and launches QuickByte when a queue comes due.
///
/// It is registered the same way QuickByte itself is — a value under
/// <c>HKCU\...\CurrentVersion\Run</c>. That is a per-user, no-elevation
/// autostart at sign-in rather than a machine-wide Windows service at boot, and
/// the choice is deliberate: a download manager that needs an administrator
/// prompt (or a service running as SYSTEM, downloading into a user's profile,
/// with that user's cookies) to schedule a queue is not a download manager
/// anyone should install. Sign-in is also the earliest moment a per-user
/// download queue means anything.
///
/// Both directions are best-effort and silent, matching
/// <see cref="StartupRegistration"/>: group policy or a security product may
/// refuse the write, and a queue window is not the place to learn about it.
/// </summary>
internal static class QueueAgentRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Value name under the Run key, and the label Task Manager shows.</summary>
    private const string ValueName = "QuickByteScheduler";

    private const string AgentFileName = "QuickByte.Agent.exe";

    /// <summary>Held by the agent for its whole life — see its Program.cs.</summary>
    private const string AgentMutexName = @"Local\QuickByte.QueueAgent";

    /// <summary>
    /// The agent as installed beside this executable. Resolved from the running
    /// app's own folder so an update, or a copy run from somewhere else, uses the
    /// agent that shipped with it rather than one left behind by a previous
    /// install.
    /// </summary>
    public static string? ExecutablePath
    {
        get
        {
            string? folder = Path.GetDirectoryName(Environment.ProcessPath ?? string.Empty);
            if (string.IsNullOrEmpty(folder)) return null;

            string path = Path.Combine(folder, AgentFileName);
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>
    /// False when QuickByte is running from a build or an install that does not
    /// include the agent. Scheduling still works while the app is open — the
    /// in-process scheduler is not the agent's job — so this is a reason to say
    /// less in the queue window, not a reason to fail.
    /// </summary>
    public static bool IsAvailable => ExecutablePath is not null;

    public static bool IsRunning
    {
        get
        {
            try
            {
                using var mutex = Mutex.OpenExisting(AgentMutexName);
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch
            {
                return false; // best-effort
            }
        }
    }

    /// <summary>
    /// Brings the agent in line with whether anything is scheduled: registered
    /// and running when it is, deregistered when it is not.
    ///
    /// Nothing here kills a running agent. It exits on its own once the queue
    /// file holds no schedules — which it reads from the same file this app just
    /// wrote — and letting it notice beats reaching into another process to end
    /// it.
    /// </summary>
    public static void Sync(bool scheduleExists)
    {
        Register(scheduleExists);

        if (scheduleExists) EnsureRunning();
    }

    /// <summary>
    /// Starts the agent now, so a schedule set this afternoon is watched this
    /// afternoon rather than from the next sign-in. Does nothing if one is
    /// already running — the agent is single-instance itself, so a duplicate
    /// launch would exit immediately anyway; this only avoids the process
    /// creation.
    /// </summary>
    public static void EnsureRunning()
    {
        if (IsRunning) return;
        if (ExecutablePath is not { } path) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path)!,
                UseShellExecute = false
            })?.Dispose();
        }
        catch
        {
            // Best-effort: the in-app scheduler still covers every queue for as
            // long as this window is open.
        }
    }

    private static void Register(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            if (ExecutablePath is not { } path) return;

            // Quoted: Run values are command lines, and an unquoted path through
            // "Program Files" is read as C:\Program.exe with arguments.
            string command = $"\"{path}\"";
            if (key.GetValue(ValueName) as string == command) return;

            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        catch
        {
            // Best-effort — see the class comment.
        }
    }
}
