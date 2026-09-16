using Microsoft.Win32;
using QuickByte.UI;

namespace QuickByte.UI.Tests;

/// <summary>
/// The Attachment Manager policy that decides whether finished downloads are
/// marked as coming from the internet. Read from a scratch key under
/// <c>HKCU\Software\QuickByte.Tests</c> — the machine half of the lookup then
/// finds nothing at that path, which is also what exercises the fallback to the
/// user's policy.
/// </summary>
public sealed class ZoneInformationPolicyTests : IDisposable
{
    private const string Root = @"Software\QuickByte.Tests";
    private readonly string _previousPath = ZoneInformationPolicy.PolicyKeyPath;
    private readonly string _path = $@"{Root}\{Guid.NewGuid():N}\Attachments";

    public ZoneInformationPolicyTests() => ZoneInformationPolicy.PolicyKeyPath = _path;

    public void Dispose()
    {
        ZoneInformationPolicy.PolicyKeyPath = _previousPath;
        try { Registry.CurrentUser.DeleteSubKeyTree(Root, throwOnMissingSubKey: false); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Without_a_policy_downloads_are_marked()
    {
        Assert.True(ZoneInformationPolicy.PreservesZoneInformation);
    }

    [Fact]
    public void The_policy_that_turns_zone_information_off_turns_it_off_here_too()
    {
        Set(1);

        Assert.False(ZoneInformationPolicy.PreservesZoneInformation);
    }

    [Fact]
    public void The_policy_that_asks_for_zone_information_keeps_it_on()
    {
        Set(2);

        Assert.True(ZoneInformationPolicy.PreservesZoneInformation);
    }

    [Fact]
    public void A_value_the_policy_does_not_define_is_read_as_the_safe_default()
    {
        // The default with the warnings is the one that cannot hurt anyone.
        Set(7);
        Assert.True(ZoneInformationPolicy.PreservesZoneInformation);

        using var key = Registry.CurrentUser.CreateSubKey(_path, writable: true)!;
        key.SetValue(ZoneInformationPolicy.ValueName, "1", RegistryValueKind.String);
        Assert.True(ZoneInformationPolicy.PreservesZoneInformation);
    }

    private void Set(int value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_path, writable: true)!;
        key.SetValue(ZoneInformationPolicy.ValueName, value, RegistryValueKind.DWord);
    }
}
