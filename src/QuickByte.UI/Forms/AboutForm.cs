using System.Drawing;
using System.Windows.Forms;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Forms;

/// <summary>
/// About box. A hand-built window rather than a MessageBox so the app's own
/// chrome — not the system dialog font and gray face — is the last thing the
/// user sees when they go looking for what this thing is.
/// </summary>
public sealed class AboutForm : Form
{
    public AboutForm()
    {
        Text = "About QuickByte";
        Width = 460;
        // Tall enough for the body paragraph now that the header carries a
        // version line as well as the name and tagline.
        Height = 386;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        Font = Theme.Ui;
        Icon = BrandIcon.App;

        var header = new Panel { Dock = DockStyle.Top, Height = 110, BackColor = Theme.Surface, Padding = new Padding(24, 22, 24, 8) };
        var icon = new PictureBox
        {
            Image = BrandIcon.CreateBitmap(48),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Left,
            Width = 56,
            BackColor = Theme.Surface
        };
        // Docked children stack last-added-on-top, so these go in bottom-up:
        // version, then tagline, then the product name.
        var titles = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 4, 0, 0), BackColor = Theme.Surface };
        titles.Controls.Add(new Label
        {
            Text = $"Version {AppVersion.Display}",
            Dock = DockStyle.Top,
            Height = 20,
            ForeColor = Theme.Accent,
            Font = Theme.UiSmallBold
        });
        titles.Controls.Add(new Label
        {
            Text = "Multi-connection download manager",
            Dock = DockStyle.Top,
            Height = 20,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall
        });
        titles.Controls.Add(new Label
        {
            Text = "QuickByte",
            Dock = DockStyle.Top,
            Height = 30,
            ForeColor = Theme.Text,
            Font = new Font("Segoe UI", 15f, FontStyle.Bold)
        });
        header.Controls.Add(titles);
        header.Controls.Add(icon);

        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(26, 0, 26, 8) };
        body.Controls.Add(new Label
        {
            Text = "Segmented HTTP downloads with pause, resume and retry — built on " +
                   "C# and WinForms with no third-party dependencies.\n\n" +
                   "The download engine runs headless in QuickByte.Core; every window " +
                   "you see is a view over the same event stream.",
            Dock = DockStyle.Fill,
            ForeColor = Theme.TextMuted,
            Font = Theme.Ui
        });

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Theme.HeaderBack, Padding = new Padding(18, 13, 18, 13) };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };
        // The four-part file version is the one that matches the .exe's
        // Properties page, so it is the one worth showing where someone filing a
        // bug will find it. Added before the button: docked children are laid
        // out last-added-first, so the button has to claim its edge first for
        // the filled label to take what's left.
        footer.Controls.Add(new Label
        {
            Text = $"Build {AppVersion.File}  ·  {AppVersion.Copyright}",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            AutoEllipsis = true
        });

        var okButton = Theme.StyleButton(new Button { Text = "Close", DialogResult = DialogResult.OK, Dock = DockStyle.Right }, primary: true);
        footer.Controls.Add(okButton);

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(header);

        AcceptButton = okButton;
        CancelButton = okButton;
    }
}
