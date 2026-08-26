using System.Globalization;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Tests;

/// <summary>
/// The tray tooltip: with the window closed, it is the only place the app's
/// state can be read at all.
///
/// The length limit is the reason this is a function rather than a string built
/// at the assignment. <c>NOTIFYICONDATA</c>'s tip is 64 characters including the
/// terminator and <see cref="System.Windows.Forms.NotifyIcon.Text"/> throws on
/// anything longer instead of trimming — from a progress tick, on a background
/// thread, in a process whose window is not on screen.
/// </summary>
public sealed class TrayIconControllerTests
{
    private const int MaxTooltipLength = 63;

    [Fact]
    public void An_idle_app_says_so()
    {
        Assert.Equal("QuickByte — idle", TrayIconController.TooltipText(0, 0));
    }

    [Fact]
    public void An_active_app_reports_what_it_is_doing()
    {
        string text = TrayIconController.TooltipText(3, 1024 * 1024);

        Assert.StartsWith("QuickByte — 3 active · ", text);
        Assert.Contains("/s", text);
    }

    [Fact]
    public void The_speed_is_dropped_when_nothing_is_running()
    {
        // Zero downloads at zero bytes a second is "idle", not "0 active".
        Assert.DoesNotContain("active", TrayIconController.TooltipText(0, 12345));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1024)]
    [InlineData(32, 12_345_678)]
    [InlineData(9999, 9.9e12)]
    [InlineData(int.MaxValue, double.MaxValue)]
    public void The_tip_never_exceeds_what_Windows_accepts(int active, double speed)
    {
        Assert.True(TrayIconController.TooltipText(active, speed).Length <= MaxTooltipLength);
    }

    [Fact]
    public void The_widest_tip_the_app_can_build_is_well_inside_the_limit()
    {
        // Worth stating plainly: the truncation in TooltipText cannot be reached
        // today. Speeds go through ByteFormatter, which casts to long and stops
        // at TB, so the widest possible tip is a ten-digit count and
        // "8388608.00 TB/s" — a little under fifty characters. The guard stays
        // because NotifyIcon throws rather than trims, and this is the test that
        // would notice if a longer prefix or a bigger unit ever closed the gap.
        string widest = TrayIconController.TooltipText(int.MaxValue, long.MaxValue);

        Assert.Equal(widest, TrayIconController.TooltipText(int.MaxValue, long.MaxValue));
        Assert.InRange(widest.Length, 1, MaxTooltipLength - 10);
    }

    [Fact]
    public void The_count_is_the_number_it_was_given()
    {
        Assert.Contains(7.ToString(CultureInfo.CurrentCulture), TrayIconController.TooltipText(7, 2048));
    }
}
