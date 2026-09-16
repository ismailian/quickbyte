using Microsoft.Win32;

namespace QuickByte.UI;

/// <summary>
/// Whether Windows is configured to keep zone information on downloaded files —
/// the Attachment Manager policy "Do not preserve zone information in file
/// attachments", which is <c>SaveZoneInformation</c> under
/// <c>...\CurrentVersion\Policies\Attachments</c>.
///
/// QuickByte marks every finished download as coming from the internet
/// (<see cref="Core.Helpers.MarkOfTheWeb"/>), the way the browsers do. An
/// administrator who turned that off for the browsers has turned it off for this
/// too, and Chrome honours the same policy.
///
/// Read here rather than in Core because it is a registry value, and the
/// registry belongs to the UI project like every other Windows concept; Core is
/// handed the answer. Read once per launch, like the rest of the snapshotted
/// configuration.
/// </summary>
internal static class ZoneInformationPolicy
{
    /// <summary>Settable only so the tests can read a scratch key instead of the real policy.</summary>
    internal static string PolicyKeyPath { get; set; } = @"Software\Microsoft\Windows\CurrentVersion\Policies\Attachments";

    internal const string ValueName = "SaveZoneInformation";

    /// <summary>
    /// True unless a policy says not to preserve zone information. The machine
    /// policy wins over the user's, as it does for any Group Policy setting.
    /// Anything unreadable, or any value other than the two the policy defines,
    /// reads as "preserve": the safe default is the one with the warnings.
    /// </summary>
    public static bool PreservesZoneInformation =>
        Read(Registry.LocalMachine) ?? Read(Registry.CurrentUser) ?? true;

    private static bool? Read(RegistryKey hive)
    {
        try
        {
            using var key = hive.OpenSubKey(PolicyKeyPath);
            return key?.GetValue(ValueName) switch
            {
                1 => false, // "Do not preserve zone information"
                2 => true,  // "Preserve zone information"
                _ => null
            };
        }
        catch
        {
            return null;
        }
    }
}
