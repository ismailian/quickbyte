using Microsoft.Win32;

namespace QuickByte.UI.Tests;

/// <summary>
/// Points both autostart registrars at a throwaway key under
/// <c>HKCU\Software\QuickByte.Tests</c> and deletes it afterwards.
///
/// The real key is <c>...\CurrentVersion\Run</c>, which holds the entry that
/// starts the user's own QuickByte at sign-in. These tests write and delete
/// values, and doing that to the real key would either remove someone's
/// autostart or point it at a test host — so the path is redirected for the
/// length of the test and the whole scratch tree is removed at the end,
/// including anything a previous run left behind.
/// </summary>
internal sealed class ScratchRunKey : IDisposable
{
    private const string Root = @"Software\QuickByte.Tests";

    private readonly string _previousStartupPath;
    private readonly string _previousAgentPath;

    public ScratchRunKey()
    {
        Path = $@"{Root}\{Guid.NewGuid():N}\Run";

        _previousStartupPath = StartupRegistration.RunKeyPath;
        _previousAgentPath = QueueAgentRegistration.RunKeyPath;

        StartupRegistration.RunKeyPath = Path;
        QueueAgentRegistration.RunKeyPath = Path;
    }

    public string Path { get; }

    /// <summary>Whether the key exists at all — a registrar that wrote nothing never creates it.</summary>
    public bool Exists
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(Path);
            return key is not null;
        }
    }

    public string? Value(string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Path);
        return key?.GetValue(name) as string;
    }

    public void SetValue(string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Path, writable: true)!;
        key.SetValue(name, value, RegistryValueKind.String);
    }

    public string[] ValueNames()
    {
        using var key = Registry.CurrentUser.OpenSubKey(Path);
        return key?.GetValueNames() ?? Array.Empty<string>();
    }

    public void Dispose()
    {
        StartupRegistration.RunKeyPath = _previousStartupPath;
        QueueAgentRegistration.RunKeyPath = _previousAgentPath;

        try { Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false); }
        catch { /* best-effort, matching the registrars' own idiom */ }
    }
}
