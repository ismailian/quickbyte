using QuickByte.Core.Models;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Tests;

/// <summary>
/// The connection picker shared by Add Download and Options.
///
/// It is a drop-down over a fixed list, and the one thing a drop-down does
/// badly is a value that is not in its list: <c>SelectedItem = 5</c> selects
/// nothing at all, and a ComboBox with nothing selected reads back as its first
/// item — one connection, silently, for every download from then on. That is
/// exactly the state a settings.json written by the old 1–32 spinner puts it
/// in, so the snapping is not a nicety.
/// </summary>
public sealed class ConnectionCountBoxTests
{
    [Fact]
    public void The_list_is_the_one_the_model_publishes()
    {
        using var box = new ConnectionCountBox();

        Assert.Equal(DownloadSettings.ConnectionChoices, box.Items.Cast<int>().ToList());
    }

    [Fact]
    public void A_fresh_box_offers_the_default_rather_than_the_first_entry()
    {
        using var box = new ConnectionCountBox();

        Assert.Equal(DownloadSettings.DefaultConnections, box.Connections);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    public void Every_listed_value_round_trips(int choice)
    {
        using var box = new ConnectionCountBox { Connections = choice };

        Assert.Equal(choice, box.Connections);
    }

    [Theory]
    [InlineData(5, 4)]
    [InlineData(3, 2)]
    [InlineData(31, 24)]
    [InlineData(12, 8)]
    public void A_number_from_the_old_spinner_snaps_down_to_a_listed_one(int stored, int expected)
    {
        using var box = new ConnectionCountBox { Connections = stored };

        Assert.Equal(expected, box.Connections);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    [InlineData(9999)]
    public void A_value_off_either_end_still_leaves_something_selected(int stored)
    {
        // A hand-edited settings.json is the only way these reach the box, and
        // "nothing selected" is the failure that has to be impossible.
        using var box = new ConnectionCountBox { Connections = stored };

        Assert.Contains(box.Connections, DownloadSettings.ConnectionChoices);
        Assert.True(box.SelectedIndex >= 0);
    }
}
