using System.Drawing;
using System.Windows.Forms;

namespace QuickByte.UI.Controls;

/// <summary>
/// Builders for the pieces every dialog in the app repeats: the icon+title
/// header, the bordered footer that holds the buttons, field labels and flat
/// text fields. Having one source for these is what keeps "Add New Download",
/// "Options" and the completion window looking like the same application
/// rather than three eras of WinForms.
/// </summary>
public static class FormChrome
{
    /// <summary>Icon + title + subtitle band, docked to the top of a dialog.</summary>
    public static Panel Header(string title, string subtitle, Bitmap icon)
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Theme.Surface, Padding = new Padding(24, 16, 24, 10) };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        var iconBox = new PictureBox
        {
            Image = icon,
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Left,
            Width = 48,
            BackColor = Theme.Surface
        };

        var text = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 1, 0, 0), BackColor = Theme.Surface };
        text.Controls.Add(new Label
        {
            Text = subtitle,
            Dock = DockStyle.Top,
            Height = 20,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            AutoEllipsis = true
        });
        text.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Top,
            Height = 26,
            ForeColor = Theme.Text,
            Font = Theme.TitleBold,
            AutoEllipsis = true
        });

        header.Controls.Add(text);
        header.Controls.Add(iconBox);
        return header;
    }

    /// <summary>Bottom band with a hairline top border, sized for 30px buttons.</summary>
    public static Panel Footer(int height = 60)
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = height, BackColor = Theme.HeaderBack, Padding = new Padding(18, 13, 18, 13) };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        return footer;
    }

    /// <summary>Right-aligned button row for a footer; add buttons in priority order.</summary>
    public static FlowLayoutPanel ButtonRow() => new()
    {
        Dock = DockStyle.Right,
        FlowDirection = FlowDirection.RightToLeft,
        AutoSize = true,
        WrapContents = false,
        BackColor = Theme.HeaderBack
    };

    public static Label FieldLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = Theme.TextMuted,
        Font = Theme.UiSmall,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 4, 8, 4)
    };

    public static TextBox Field() => new()
    {
        Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.FixedSingle,
        BackColor = Theme.Surface,
        ForeColor = Theme.Text,
        Font = Theme.Ui,
        Margin = new Padding(0, 6, 0, 6)
    };

    /// <summary>Caption + value pair inside a two-per-row info grid.</summary>
    public static Label AddInfoCell(TableLayoutPanel grid, string caption, int column, int row, bool span = false)
    {
        grid.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 6, 5)
        }, column, row);

        var value = new Label
        {
            Text = "—",
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 3, 6, 3)
        };
        grid.Controls.Add(value, column + 1, row);
        if (span) grid.SetColumnSpan(value, 3);
        return value;
    }
}
