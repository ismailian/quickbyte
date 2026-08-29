using System.Windows.Forms;
using QuickByte.Core.Models;

namespace QuickByte.UI.Controls;

/// <summary>
/// The "how many connections" picker, shared by Add Download and Options so the
/// two cannot drift apart.
///
/// A fixed list rather than the 1–32 spinner it replaces. A spinner presents 32
/// answers and no opinion about any of them, and it invites the user to walk to
/// the number one click at a time; the values it offers that this does not —
/// 3, 5, 13 — are not meaningfully different from their neighbours, because what
/// decides the speed is roughly how many sockets the server will serve at once.
/// <see cref="DownloadSettings.ConnectionChoices"/> holds the list.
/// </summary>
public sealed class ConnectionCountBox : ComboBox
{
    public ConnectionCountBox()
    {
        // DropDownList, so the only values that can reach Connections are the
        // ones in the list — an editable combo would let a typed 7 back in and
        // put the read below on the fallback path.
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Ui;

        foreach (int choice in DownloadSettings.ConnectionChoices)
            Items.Add(choice);

        Connections = DownloadSettings.DefaultConnections;
    }

    /// <summary>
    /// The selected count. Setting it snaps to the nearest listed value at or
    /// below the one given, which is what carries a number written by an older
    /// build — or by hand into settings.json — into a list that no longer offers
    /// it. Assigning an unlisted value directly would select nothing, and a
    /// combo box with nothing selected reads back as its first item: one
    /// connection, silently, for every download from then on.
    /// </summary>
    public int Connections
    {
        get => SelectedItem is int value ? value : DownloadSettings.ConnectionChoices[0];
        set => SelectedItem = DownloadSettings.NearestConnectionChoice(value);
    }
}
