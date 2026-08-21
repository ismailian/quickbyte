using System.Drawing;
using System.Windows.Forms;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Forms;

/// <summary>
/// Exposes every configurable option, grouped into tabs: connection defaults
/// and retry policy, folders, and window behaviour. Saving pushes a new
/// <see cref="DownloadSettings"/> through <see cref="ISettingsService"/>.
///
/// Note that <see cref="OnSaveClicked"/> builds a <em>fresh</em>
/// <see cref="DownloadSettings"/> — any field not copied here silently reverts
/// to its default, so a new setting must be added in three places: the model,
/// a control below, and the object built in <see cref="OnSaveClicked"/>.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly ISettingsService _settingsService;

    private NumericUpDown _defaultConnectionsUpDown = null!;
    private NumericUpDown _maxRetriesUpDown = null!;
    private NumericUpDown _retryDelayUpDown = null!;
    private NumericUpDown _maxConcurrentUpDown = null!;
    private NumericUpDown _globalSpeedLimitUpDown = null!;
    private NumericUpDown _progressIntervalUpDown = null!;
    private TextBox _downloadFolderTextBox = null!;
    private TextBox _tempFolderTextBox = null!;
    private CheckBox _autoOpenDetailsCheckBox = null!;
    private CheckBox _showCompletionCheckBox = null!;

    public SettingsForm(ISettingsService settingsService)
    {
        _settingsService = settingsService;
        BuildUi();
        LoadValues();
    }

    private void BuildUi()
    {
        Text = "QuickByte Options";
        Width = 600;
        // Sized for the tallest tab — Connection, at five rows of 58 px.
        Height = 560;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Theme.Surface;
        Font = Theme.Ui;
        Icon = BrandIcon.App;

        var tabs = new FlatTabView { Dock = DockStyle.Fill };
        BuildConnectionTab(tabs.AddPage("Connection"));
        BuildFoldersTab(tabs.AddPage("Folders"));
        BuildInterfaceTab(tabs.AddPage("Interface"));

        Controls.Add(tabs);
        Controls.Add(BuildFooter());
        Controls.Add(FormChrome.Header("Options", "Defaults for new downloads and how the app behaves.", IconFactory.Settings(40)));
    }

    private void BuildConnectionTab(Panel page)
    {
        var layout = NewGrid(page, rows: 5);
        int row = 0;

        _defaultConnectionsUpDown = AddNumericRow(layout, row++,
            "Default connections",
            $"Segments used for each new download ({DownloadSettings.MinConnections}–{DownloadSettings.MaxConnections}).",
            DownloadSettings.MinConnections, DownloadSettings.MaxConnections);

        _maxConcurrentUpDown = AddNumericRow(layout, row++,
            "Max concurrent downloads",
            "How many run at once; the rest queue. Applies after restart.",
            1, 20);

        _globalSpeedLimitUpDown = AddNumericRow(layout, row++,
            "Global speed limit (KB/s)",
            "Shared by all downloads; 0 = no limit. Applies at once.",
            0, 1_000_000, increment: 50);

        _maxRetriesUpDown = AddNumericRow(layout, row++,
            "Max retries per connection",
            "Attempts before a connection is marked failed.",
            0, 20);

        _retryDelayUpDown = AddNumericRow(layout, row++,
            "Retry base delay (ms)",
            "Grows with exponential backoff on each successive retry.",
            100, 60000, increment: 100);

    }

    private void BuildFoldersTab(Panel page)
    {
        var layout = NewGrid(page, rows: 2);
        int row = 0;

        _downloadFolderTextBox = AddFolderRow(layout, row++, "Default download folder", "Where finished files are saved.");
        _tempFolderTextBox = AddFolderRow(layout, row++, "Temp folder", "Holds part files while a download is in flight.");

    }

    private void BuildInterfaceTab(Panel page)
    {
        var layout = NewGrid(page, rows: 3);
        int row = 0;

        _progressIntervalUpDown = AddNumericRow(layout, row++,
            "Progress sampling (ms)",
            "Windows interpolate between samples — lower is smoother.",
            50, 2000, increment: 50);

        _autoOpenDetailsCheckBox = AddCheckRow(layout, row++,
            "Open the details window automatically when a download starts");

        _showCompletionCheckBox = AddCheckRow(layout, row++,
            "Show the download complete window when a download finishes");

    }

    // ------------------------------------------------------------ Grid bits --

    /// <summary>
    /// Rows are declared up front (count and styles): a TableLayoutPanel with an
    /// unset RowCount re-flows explicitly positioned controls into the wrong
    /// cells once a row is spanned.
    /// </summary>
    private static TableLayoutPanel NewGrid(Panel page, int rows)
    {
        page.Padding = new Padding(22, 16, 22, 12);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = rows + 1,
            BackColor = Theme.Surface
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));

        for (int i = 0; i < rows; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // filler

        page.Controls.Add(layout);
        return layout;
    }

    private static NumericUpDown AddNumericRow(TableLayoutPanel layout, int row, string caption, string hint, int min, int max, int increment = 1)
    {
        var captionBlock = CaptionBlock(caption, hint);
        layout.Controls.Add(captionBlock, 0, row);
        layout.SetColumnSpan(captionBlock, 2);

        var upDown = new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Increment = increment,
            Width = 88,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Ui,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 12, 0, 0),
            TextAlign = HorizontalAlignment.Right
        };
        layout.Controls.Add(upDown, 2, row);
        return upDown;
    }

    private TextBox AddFolderRow(TableLayoutPanel layout, int row, string caption, string hint)
    {
        layout.Controls.Add(CaptionBlock(caption, hint), 0, row);

        var textBox = FormChrome.Field();
        textBox.Margin = new Padding(0, 12, 8, 0);
        layout.Controls.Add(textBox, 1, row);

        var browse = Theme.StyleButton(new Button { Text = "Browse…", Anchor = AnchorStyles.Right, Margin = new Padding(0, 10, 0, 0) });
        browse.Click += (_, _) => Browse(textBox);
        layout.Controls.Add(browse, 2, row);
        return textBox;
    }

    private static CheckBox AddCheckRow(TableLayoutPanel layout, int row, string caption)
    {
        var box = new CheckBox
        {
            Text = caption,
            AutoSize = true,
            Font = Theme.Ui,
            ForeColor = Theme.Text,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 14, 0, 0),
            Anchor = AnchorStyles.Left
        };
        layout.Controls.Add(box, 0, row);
        layout.SetColumnSpan(box, 3);
        return box;
    }

    private static Panel CaptionBlock(string caption, string hint)
    {
        var panel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = new Padding(0, 8, 8, 0) };
        panel.Controls.Add(new Label
        {
            Text = hint,
            Dock = DockStyle.Top,
            Height = 30,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall
        });
        panel.Controls.Add(new Label
        {
            Text = caption,
            Dock = DockStyle.Top,
            Height = 18,
            ForeColor = Theme.Text,
            Font = Theme.UiBold
        });
        return panel;
    }

    private Panel BuildFooter()
    {
        var footer = FormChrome.Footer();
        var buttons = FormChrome.ButtonRow();

        var saveButton = Theme.StyleButton(new Button { Text = "Save" }, primary: true);
        saveButton.Click += (_, _) => OnSaveClicked();
        var cancelButton = Theme.StyleButton(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel });

        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(cancelButton);
        footer.Controls.Add(buttons);

        AcceptButton = saveButton;
        CancelButton = cancelButton;
        return footer;
    }

    private void LoadValues()
    {
        var s = _settingsService.Current;
        _defaultConnectionsUpDown.Value = Math.Clamp(s.DefaultConnectionsCount, DownloadSettings.MinConnections, DownloadSettings.MaxConnections);
        _maxRetriesUpDown.Value = Math.Clamp(s.MaxRetries, 0, 20);
        _retryDelayUpDown.Value = Math.Clamp(s.RetryDelayMilliseconds, 100, 60000);
        _maxConcurrentUpDown.Value = Math.Clamp(s.MaxConcurrentDownloads, 1, 20);
        _globalSpeedLimitUpDown.Value = Math.Clamp(s.GlobalSpeedLimitBytesPerSecond / ByteFormatter.BytesPerKilobyte, 0, 1_000_000);
        _progressIntervalUpDown.Value = Math.Clamp(s.ProgressUpdateIntervalMilliseconds, 50, 2000);
        _downloadFolderTextBox.Text = s.DefaultDownloadFolder;
        _tempFolderTextBox.Text = s.TempFolder;
        _autoOpenDetailsCheckBox.Checked = s.AutoOpenDetailsWindow;
        _showCompletionCheckBox.Checked = s.ShowCompletionWindow;
    }

    private void Browse(TextBox target)
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = target.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            target.Text = dialog.SelectedPath;
    }

    private void InitializeComponent()
    {
        SuspendLayout();
        // 
        // SettingsForm
        // 
        ClientSize = new Size(282, 253);
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "SettingsForm";
        ResumeLayout(false);

    }

    private void OnSaveClicked()
    {
        var updated = new DownloadSettings
        {
            DefaultConnectionsCount = (int)_defaultConnectionsUpDown.Value,
            MaxRetries = (int)_maxRetriesUpDown.Value,
            RetryDelayMilliseconds = (int)_retryDelayUpDown.Value,
            MaxConcurrentDownloads = (int)_maxConcurrentUpDown.Value,
            GlobalSpeedLimitBytesPerSecond = (long)_globalSpeedLimitUpDown.Value * ByteFormatter.BytesPerKilobyte,
            ProgressUpdateIntervalMilliseconds = (int)_progressIntervalUpDown.Value,
            DefaultDownloadFolder = _downloadFolderTextBox.Text.Trim(),
            TempFolder = _tempFolderTextBox.Text.Trim(),
            AutoOpenDetailsWindow = _autoOpenDetailsCheckBox.Checked,
            ShowCompletionWindow = _showCompletionCheckBox.Checked,
            // No UI field — carried forward explicitly so it doesn't reset.
            StreamBufferSizeBytes = _settingsService.Current.StreamBufferSizeBytes
        };

        try
        {
            Directory.CreateDirectory(updated.DefaultDownloadFolder);
            Directory.CreateDirectory(updated.TempFolder);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not create folder: {ex.Message}", "Options", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        _settingsService.Save(updated);
        DialogResult = DialogResult.OK;
        Close();
    }
}
