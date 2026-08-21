using System.Drawing;
using System.Windows.Forms;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Forms;

/// <summary>
/// The window IDM pops when a transfer finishes: a green tick, the file's
/// final stats, and the two things anyone actually wants next — open the file
/// or show it in Explorer. Opened by <see cref="MainForm"/> in place of the
/// details window (which closes), so one download never leaves two windows
/// describing it.
///
/// The "show this window" checkbox writes straight through
/// <see cref="ISettingsService"/>, so opting out here is permanent.
/// </summary>
public sealed class DownloadCompleteForm : Form
{
    private readonly DownloadItem _item;
    private readonly ISettingsService _settingsService;

    public DownloadCompleteForm(DownloadItem item, ISettingsService settingsService)
    {
        _item = item;
        _settingsService = settingsService;
        BuildUi();
    }

    private void BuildUi()
    {
        Text = "Download Complete";
        Width = 580;
        Height = 320;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        Font = Theme.Ui;
        Icon = CreateIcon();

        Controls.Add(BuildBody());
        Controls.Add(BuildFooter());
        Controls.Add(BuildHeader());
    }

    private static Icon CreateIcon()
    {
        using var bmp = IconFactory.CheckCircle(32);
        return Icon.FromHandle(bmp.GetHicon());
    }

    private Panel BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 84, BackColor = Theme.Surface, Padding = new Padding(22, 18, 22, 12) };

        var icon = new PictureBox
        {
            Image = IconFactory.CheckCircle(44),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Left,
            Width = 52,
            BackColor = Theme.Surface
        };

        // The file name lives in the card below with everything else, so the
        // header is just the confirmation.
        var text = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 0, 0, 0), BackColor = Theme.Surface };
        text.Controls.Add(new Label
        {
            Text = "Download complete",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = Theme.TitleBold,
            ForeColor = Theme.Text
        });

        header.Controls.Add(text);
        header.Controls.Add(icon);
        return header;
    }

    private Panel BuildBody()
    {
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(22, 4, 22, 4) };

        var card = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Theme.HeaderBack, Padding = new Padding(14, 10, 14, 10) };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };

        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
            BackColor = Theme.HeaderBack
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++)
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // absorbs leftover height

        AddCell(grid, "File:", _item.FileName, 0);
        AddCell(grid, "Size:", ByteFormatter.FormatBytes(_item.TotalBytes), 1);
        AddCell(grid, "Saved to:", _item.SaveFolder, 2);

        card.Controls.Add(grid);
        body.Controls.Add(card);
        return body;
    }

    private static void AddCell(TableLayoutPanel grid, string caption, string value, int row)
    {
        grid.Controls.Add(new Label
        {
            Text = caption,
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 4, 6, 4)
        }, 0, row);

        grid.Controls.Add(new Label
        {
            Text = value,
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 3, 8, 3)
        }, 1, row);
    }

    private Panel BuildFooter()
    {
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.HeaderBack, Padding = new Padding(18, 13, 18, 13) };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            WrapContents = false,
            BackColor = Theme.HeaderBack
        };

        var openFile = Theme.StyleButton(new Button { Text = "Open File" }, primary: true);
        openFile.Enabled = File.Exists(_item.FullPath);
        openFile.Click += (_, _) => { FileLauncher.OpenFile(_item.FullPath); Close(); };

        var openFolder = Theme.StyleButton(new Button { Text = "Open Folder", Width = 108 });
        openFolder.Click += (_, _) => { FileLauncher.RevealInExplorer(_item.FullPath, _item.SaveFolder); Close(); };

        var close = Theme.StyleButton(new Button { Text = "Close", DialogResult = DialogResult.Cancel });
        close.Click += (_, _) => Close();

        buttons.Controls.Add(openFile);
        buttons.Controls.Add(openFolder);
        buttons.Controls.Add(close);

        var showAgain = new CheckBox
        {
            Text = "Show this window next time",
            Dock = DockStyle.Left,
            AutoSize = true,
            Checked = _settingsService.Current.ShowCompletionWindow,
            Font = Theme.UiSmall,
            ForeColor = Theme.TextMuted,
            BackColor = Theme.HeaderBack,
            Padding = new Padding(0, 6, 0, 0)
        };
        showAgain.CheckedChanged += (_, _) => SaveShowCompletionPreference(showAgain.Checked);

        footer.Controls.Add(buttons);
        footer.Controls.Add(showAgain);

        AcceptButton = openFile.Enabled ? openFile : close;
        CancelButton = close;
        return footer;
    }

    private void SaveShowCompletionPreference(bool show)
    {
        var current = _settingsService.Current;
        if (current.ShowCompletionWindow == show) return;

        current.ShowCompletionWindow = show;
        _settingsService.Save(current);
    }
}
