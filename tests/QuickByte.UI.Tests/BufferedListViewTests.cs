using System.Drawing;
using System.Windows.Forms;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Tests;

/// <summary>
/// The owner-draw cycle the wrapped Win32 ListView runs outside a paint message
/// when the pointer crosses a row.
///
/// It raises DrawItem for that row and a single DrawSubItem for column 0, and
/// because no WM_PAINT is in progress the double buffer is not involved: a
/// handler that fills the row background there paints it onto the screen and
/// leaves every other column blank until something repaints them. That is what
/// the download lists flickered with, and it is invisible in the source —
/// nothing distinguishes the stray callback from a real one except the message
/// being serviced when it arrives, so these pin both halves: the stray cycle
/// reaches nobody, and an honest repaint still reaches everybody.
///
/// The control needs a window for any of this (the notifications come from
/// comctl32), but not a visible one, so no form is constructed here.
/// </summary>
public sealed class BufferedListViewTests : IDisposable
{
    private const int WmMouseMove = 0x0200;

    private readonly BufferedListView _list = new() { Size = new Size(420, 260) };
    private readonly List<int> _itemDraws = new();
    private readonly List<int> _subItemDraws = new();

    public BufferedListViewTests()
    {
        _list.Columns.Add("Name", 160);
        _list.Columns.Add("Size", 80);
        _list.Columns.Add("Progress", 90);
        _list.Columns.Add("Status", 90);

        for (int i = 0; i < 6; i++)
        {
            var row = new ListViewItem($"file{i}.zip");
            row.SubItems.Add("10 MB");
            row.SubItems.Add(string.Empty);
            row.SubItems.Add("Downloading");
            _list.Items.Add(row);
        }

        _list.DrawItem += (_, e) => _itemDraws.Add(e.ItemIndex);
        _list.DrawSubItem += (_, e) => _subItemDraws.Add(e.ColumnIndex);
        _list.CreateControl();
    }

    public void Dispose() => _list.Dispose();

    private void HoverRow(int index)
    {
        var bounds = _list.Items[index].Bounds;
        int x = bounds.Left + 8;
        int y = bounds.Top + bounds.Height / 2;
        _list.DeliverMessageForTest(WmMouseMove, (IntPtr)((y << 16) | (x & 0xFFFF)));
    }

    private void Paint()
    {
        using var bitmap = new Bitmap(_list.Width, _list.Height);
        _list.DrawToBitmap(bitmap, new Rectangle(Point.Empty, _list.Size));
    }

    [Fact]
    public void The_rows_have_somewhere_to_be_hovered()
    {
        // Guards the guard. Every "raises nothing" assertion below would pass
        // just as well against a list whose rows were never laid out, because
        // the message would land on empty space.
        Assert.True(_list.IsHandleCreated);
        Assert.True(_list.Items[2].Bounds.Height > 0);
        Assert.True(_list.Items[2].Bounds.Width > 0);
    }

    [Fact]
    public void Crossing_a_row_raises_no_item_draw()
    {
        HoverRow(2);

        Assert.Empty(_itemDraws);
    }

    [Fact]
    public void Crossing_a_row_raises_no_cell_draw()
    {
        // The stray cycle offers column 0 and nothing else. Letting it through
        // redraws the file name over itself and leaves the other columns to be
        // painted over by the item background.
        HoverRow(2);

        Assert.Empty(_subItemDraws);
    }

    [Fact]
    public void Crossing_every_row_in_turn_raises_nothing()
    {
        // The control offers the cycle once per row per invalidation, so a
        // pointer dragged down the list is the worst case, not one move.
        for (int i = 0; i < _list.Items.Count; i++) HoverRow(i);

        Assert.Empty(_itemDraws);
        Assert.Empty(_subItemDraws);
    }

    [Fact]
    public void Painting_still_reaches_the_draw_handlers()
    {
        Paint();

        Assert.NotEmpty(_itemDraws);
        Assert.NotEmpty(_subItemDraws);
    }

    [Fact]
    public void Painting_offers_every_column_of_a_row_not_just_the_first()
    {
        // The difference between a repaint and the stray cycle, stated as the
        // property the painting code relies on: a row that is drawn is drawn
        // whole. A guard that swallowed real paints would still pass the
        // NotEmpty check above.
        Paint();

        Assert.Equal(new[] { 0, 1, 2, 3 }, _subItemDraws.Take(4));
    }

    [Fact]
    public void A_row_crossed_before_a_paint_is_still_painted_whole()
    {
        HoverRow(1);
        Paint();

        Assert.Contains(1, _itemDraws);
        Assert.Equal(new[] { 0, 1, 2, 3 }, _subItemDraws.Take(4));
    }
}
