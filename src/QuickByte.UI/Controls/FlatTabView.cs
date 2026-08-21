using System.Drawing;
using System.Windows.Forms;

namespace QuickByte.UI.Controls;

/// <summary>
/// A tab strip + page host that looks like the rest of the app.
///
/// Stands in for <see cref="TabControl"/>, whose visual-styles rendering
/// (beveled tabs on a gray page background) is the single most dated-looking
/// thing in a WinForms window and can't be restyled without owner-drawing the
/// whole control anyway. Pages are plain panels swapped by visibility, so
/// nothing re-creates handles when the user switches tabs.
/// </summary>
public sealed class FlatTabView : Panel
{
    private readonly Panel _header;
    private readonly Panel _content;
    private readonly List<TabButton> _buttons = new();
    private readonly List<Panel> _pages = new();
    private int _selectedIndex = -1;

    public FlatTabView()
    {
        DoubleBuffered = true;
        BackColor = Theme.Surface;

        _content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };
        _header = new Panel { Dock = DockStyle.Top, Height = 38, BackColor = Theme.HeaderBack };
        _header.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, _header.Height - 1, _header.Width, _header.Height - 1);
        };

        Controls.Add(_content);
        Controls.Add(_header);
    }

    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (value < 0 || value >= _pages.Count || value == _selectedIndex) return;
            _selectedIndex = value;
            for (int i = 0; i < _pages.Count; i++)
            {
                _pages[i].Visible = i == value;
                _buttons[i].Selected = i == value;
            }
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Adds a tab and returns the page panel to fill with content.</summary>
    public Panel AddPage(string title)
    {
        var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Visible = false };
        _content.Controls.Add(page);
        _pages.Add(page);

        var button = new TabButton(title) { Dock = DockStyle.Left };
        int index = _buttons.Count;
        button.Click += (_, _) => SelectedIndex = index;
        _buttons.Add(button);

        // Docked-left children stack right-to-left in add order, so re-add the
        // whole set in reverse to keep tabs reading left-to-right.
        _header.Controls.Clear();
        for (int i = _buttons.Count - 1; i >= 0; i--)
            _header.Controls.Add(_buttons[i]);

        if (_selectedIndex < 0)
        {
            _selectedIndex = 0;
            page.Visible = true;
            button.Selected = true;
        }

        return page;
    }

    /// <summary>One tab: flat label, accent underline when active, tinted on hover.</summary>
    private sealed class TabButton : Control
    {
        private bool _selected;
        private bool _hovered;

        public TabButton(string text)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Text = text;
            Font = Theme.Ui;
            Cursor = Cursors.Hand;
            BackColor = Theme.HeaderBack;
            Width = TextRenderer.MeasureText(text, Theme.UiBold).Width + 34;
        }

        public bool Selected
        {
            get => _selected;
            set { if (_selected == value) return; _selected = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(_selected ? Theme.Surface : _hovered ? Theme.AccentSoft : Theme.HeaderBack);

            var textColor = _selected ? Theme.Accent : Theme.TextMuted;
            TextRenderer.DrawText(g, Text, _selected ? Theme.UiBold : Theme.Ui,
                new Rectangle(0, 0, Width, Height - 2), textColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            using var pen = new Pen(Theme.Border);
            g.DrawLine(pen, 0, Height - 1, Width, Height - 1);

            if (!_selected) return;
            using var accent = new SolidBrush(Theme.Accent);
            g.FillRectangle(accent, 0, Height - 3, Width, 3);
        }
    }
}
