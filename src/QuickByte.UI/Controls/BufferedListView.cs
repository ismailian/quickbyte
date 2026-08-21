using System.Drawing;
using System.Windows.Forms;

namespace QuickByte.UI.Controls;

/// <summary>
/// A details-view ListView that repaints without flickering and draws its own
/// flat chrome.
///
/// The stock ListView erases its background before every paint, so the
/// high-frequency in-place cell updates the download lists do (several per
/// second, per row) show up as a visible flash. This subclass turns on
/// double buffering, swallows WM_ERASEBKGND, and owner-draws the header,
/// row backgrounds and hover state so both lists in the app look identical.
///
/// Callers still handle <see cref="ListView.DrawSubItem"/> to paint cell
/// content, and should use <see cref="SetSubItemText"/> rather than assigning
/// <c>SubItems[i].Text</c> directly — assigning invalidates the row even when
/// the text is unchanged.
/// </summary>
public class BufferedListView : ListView
{
    private const int WmEraseBkgnd = 0x0014;

    private int _hoveredIndex = -1;

    public BufferedListView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

        View = View.Details;
        OwnerDraw = true;
        FullRowSelect = true;
        HideSelection = false;
        GridLines = false;
        BorderStyle = BorderStyle.None;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Ui;
    }

    /// <summary>
    /// Sets the row height the only way a details-view ListView allows: by
    /// giving it an ImageList of that height. A 1px-wide spacer is used because
    /// row icons are painted by hand in DrawSubItem (an ImageList would rescale
    /// them to the row height and smear them).
    /// </summary>
    public void SetRowHeight(int height)
    {
        if (SmallImageList is not null) return;
        SmallImageList = new ImageList { ImageSize = new Size(1, Math.Clamp(height, 1, 256)), ColorDepth = ColorDepth.Depth32Bit };
    }

    /// <summary>Assigns cell text only when it actually changed (avoids needless repaints).</summary>
    public static void SetSubItemText(ListViewItem row, int index, string text)
    {
        if (index < 0 || index >= row.SubItems.Count) return;
        if (row.SubItems[index].Text == text) return;
        row.SubItems[index].Text = text;
    }

    /// <summary>Background for a row, accounting for selection, hover and banding.</summary>
    public Color RowBackColor(int index, bool selected)
    {
        if (selected) return Focused ? Theme.RowSelected : Theme.RowSelectedInactive;
        if (index == _hoveredIndex) return Theme.AccentSoft;
        return index % 2 == 0 ? Theme.Surface : Theme.RowAlt;
    }

    /// <summary>Foreground that stays legible on top of <see cref="RowBackColor"/>.</summary>
    public Color RowForeColor(int index, bool selected, Color preferred) =>
        selected && Focused ? Theme.TextOnAccent : preferred;

    protected override void WndProc(ref Message m)
    {
        // Double buffering already paints the full client area; letting the
        // control erase first is pure flicker.
        if (m.Msg == WmEraseBkgnd && IsHandleCreated)
        {
            m.Result = 1;
            return;
        }
        base.WndProc(ref m);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int index = GetItemAt(e.X, e.Y)?.Index ?? -1;
        if (index == _hoveredIndex) return;

        int previous = _hoveredIndex;
        _hoveredIndex = index;
        InvalidateRow(previous);
        InvalidateRow(index);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoveredIndex < 0) return;

        int previous = _hoveredIndex;
        _hoveredIndex = -1;
        InvalidateRow(previous);
    }

    private void InvalidateRow(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        Invalidate(Items[index].Bounds);
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        using (var back = new SolidBrush(Theme.HeaderBack))
            e.Graphics.FillRectangle(back, e.Bounds);

        using (var pen = new Pen(Theme.Border))
        {
            e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            if (e.ColumnIndex > 0)
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Top + 6, e.Bounds.Left, e.Bounds.Bottom - 7);
        }

        var textBounds = Rectangle.Inflate(e.Bounds, -6, 0);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    (e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, Theme.UiSmallBold, textBounds, Theme.TextMuted, flags);

        base.OnDrawColumnHeader(e);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        using (var back = new SolidBrush(RowBackColor(e.ItemIndex, e.Item?.Selected ?? false)))
            e.Graphics.FillRectangle(back, e.Bounds);

        base.OnDrawItem(e);
    }
}
