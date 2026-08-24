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
/// the text is unchanged. For repaints they should go through
/// <see cref="InvalidateRow"/> / <see cref="InvalidateCell"/> rather than
/// <c>Invalidate(row.Bounds)</c>, which silently repaints the *whole control*
/// when the row has no bounds yet.
/// </summary>
public class BufferedListView : ListView
{
    private const int WmEraseBkgnd = 0x0014;
    private const int WmHScroll = 0x0114;
    private const int WmVScroll = 0x0115;

    // Tracked as the item itself, not its index: rows are inserted and removed
    // as downloads change category (see MainForm.SetRowVisible), and a stored
    // index silently starts pointing at whichever row slid into that slot — so
    // the highlight jumps to a row the pointer isn't over.
    private ListViewItem? _hoveredItem;

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
    public Color RowBackColor(ListViewItem? row, int index, bool selected)
    {
        if (selected) return Focused ? Theme.RowSelected : Theme.RowSelectedInactive;
        if (ReferenceEquals(row, _hoveredItem)) return Theme.AccentSoft;
        return index % 2 == 0 ? Theme.Surface : Theme.RowAlt;
    }

    /// <summary>Foreground that stays legible on top of <see cref="RowBackColor"/>.</summary>
    public Color RowForeColor(int index, bool selected, Color preferred) =>
        selected && Focused ? Theme.TextOnAccent : preferred;

    /// <summary>
    /// Repaints one row. Prefer this over <c>Invalidate(row.Bounds)</c>: a row
    /// that isn't laid out yet has empty bounds, and
    /// <see cref="Control.Invalidate(Rectangle)"/> treats an empty rectangle as
    /// "invalidate everything" — one stray call repaints every row in the list.
    /// </summary>
    public void InvalidateRow(ListViewItem? row)
    {
        var bounds = RowBounds(row);
        if (!bounds.IsEmpty) Invalidate(bounds);
    }

    /// <summary>
    /// Repaints a single cell. Animated progress moves ~60 times a second while
    /// only one cell's contents change; invalidating the whole row would re-run
    /// every column's owner-draw — an icon blit and six text runs — over the row
    /// the pointer is sitting on, which is exactly the churn that reads as
    /// flicker. Falls back to the full row when the cell has no bounds of its own.
    /// </summary>
    public void InvalidateCell(ListViewItem? row, int columnIndex)
    {
        if (row?.ListView != this || !IsHandleCreated) return; // see RowBounds
        if (columnIndex < 0 || columnIndex >= row.SubItems.Count) { InvalidateRow(row); return; }

        var bounds = row.SubItems[columnIndex].Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) { InvalidateRow(row); return; }
        Invalidate(bounds);
    }

    private Rectangle RowBounds(ListViewItem? row)
    {
        if (row?.ListView != this) return Rectangle.Empty;

        // Reading ListViewItem.Bounds goes through a window message, and touching
        // Handle *creates* the window. There is nothing on screen to repaint
        // while the app sits in the tray — including a whole session of one, if
        // it started minimized — so ask nothing of a control that has no window.
        if (!IsHandleCreated) return Rectangle.Empty;

        var bounds = row.Bounds;
        return bounds.Width <= 0 || bounds.Height <= 0 ? Rectangle.Empty : bounds;
    }

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

        // Scrolling moves rows under a stationary pointer, so the hovered row
        // changes without a single mouse-move arriving. Without this the
        // highlight stays on the slot rather than the row, then snaps elsewhere
        // on the next pixel of movement.
        if (m.Msg is WmVScroll or WmHScroll) RefreshHoverFromCursor();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        RefreshHoverFromCursor();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHovered(GetItemAt(e.X, e.Y));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        // MouseLeave also fires for things that don't mean the pointer left the
        // row — the scrollbar or a context menu taking the mouse, a tooltip
        // appearing over it. Dropping the highlight on those blinks a row the
        // pointer never left, so confirm it is genuinely outside first.
        if (IsHandleCreated && ClientRectangle.Contains(PointToClient(Cursor.Position))) return;
        SetHovered(null);
    }

    private void RefreshHoverFromCursor()
    {
        if (!IsHandleCreated) return;
        var point = PointToClient(Cursor.Position);
        SetHovered(ClientRectangle.Contains(point) ? GetItemAt(point.X, point.Y) : null);
    }

    private void SetHovered(ListViewItem? row)
    {
        if (ReferenceEquals(row, _hoveredItem)) return;

        var previous = _hoveredItem;
        _hoveredItem = row;

        // Two calls rather than one union rectangle: Windows accumulates the
        // update area as a region, so the rows between a far-apart pair are
        // never dragged into the repaint.
        InvalidateRow(previous);
        InvalidateRow(row);
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
        using (var back = new SolidBrush(RowBackColor(e.Item, e.ItemIndex, e.Item?.Selected ?? false)))
            e.Graphics.FillRectangle(back, e.Bounds);

        base.OnDrawItem(e);
    }
}
