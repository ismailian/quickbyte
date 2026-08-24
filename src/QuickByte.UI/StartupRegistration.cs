using Microsoft.Win32;

namespace QuickByte.UI;

/// <summary>
/// Puts QuickByte in — or takes it out of — the list of programs Windows starts
/// when the user signs in.
///
/// The entry goes under <c>HKCU\...\CurrentVersion\Run</c> rather than the
/// machine-wide <c>HKLM</c> hive or a scheduled task: it is a per-user
/// preference, it needs no elevation, and it is the one list Task Manager's
/// Startup tab and Settings &gt; Apps &gt; Startup both show — a user who turns
/// QuickByte off there has to be able to find it.
///
/// Everything here is best-effort. Group policy, a locked-down profile or a
/// security product can all refuse the write, and none of that is a reason to
/// fail a save or a launch: the checkbox reports what happened and the app
/// carries on. <see cref="Sync"/> re-asserts the entry on every start so the
/// stored path follows the executable after an update or a move.
///
/// The preference itself lives in <see cref="Core.Models.DownloadSettings"/>;
/// this is the only place that knows it means a registry value.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>Value name under the Run key. Also the label Task Manager shows.</summary>
    private const string ValueName = "QuickByte";

    /// <summary>
    /// Quoted, because <c>Run</c> values are command lines: an unquoted path
    /// through <c>C:\Program Files\...</c> is read as an attempt to run
    /// <c>C:\Program.exe</c> with arguments.
    /// </summary>
    private static string? CommandLine =>
        Environment.ProcessPath is { Length: > 0 } path ? $"\"{path}\"" : null;

    /// <summary>Whether the Run key currently names this executable.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch
            {
                return false; // best-effort — an unreadable key reads as "not registered"
            }
        }
    }

    /// <summary>
    /// Writes or removes the entry. Returns false and the reason when Windows
    /// refuses, so the caller can say so instead of silently disagreeing with
    /// its own checkbox.
    /// </summary>
    public static bool TryApply(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "the Windows startup key could not be opened";
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            string? command = CommandLine;
            if (command is null)
            {
                error = "the running executable's path is unknown";
                return false;
            }

            key.SetValue(ValueName, command, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Brings the registry back in line with the saved preference at startup.
    /// Silent by design — nobody asked for it, and the place to be told the
    /// write failed is the Options page where the box was ticked.
    ///
    /// It matters on more than a first run: an installed copy that is updated,
    /// moved or reinstalled elsewhere leaves a stale path behind, and re-writing
    /// the value is what keeps "start with Windows" pointing at the QuickByte
    /// that is actually here.
    /// </summary>
    public static void Sync(bool enabled)
    {
        // Only ever writes when it has to: an unchanged value is a registry write
        // on every launch for nothing, and a delete of a value we never own.
        if (!enabled && !IsEnabled) return;
        if (enabled && CurrentValue() == CommandLine) return;

        TryApply(enabled, out _);
    }

    private static string? CurrentValue()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) as string;
        }
        catch
        {
            return null; // best-effort
        }
    }
}
