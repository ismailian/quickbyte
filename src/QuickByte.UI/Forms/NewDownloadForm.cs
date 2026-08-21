using System.Drawing;
using System.Windows.Forms;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.Core.Services;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Forms;

/// <summary>
/// "Add Download" dialog. Lets the user paste a URL, fetches file info (name,
/// size, content type, range support) via <see cref="IRemoteFileInfoProvider"/>,
/// and lets them adjust the save name/folder and connection count (1–32)
/// before handing everything back to <see cref="MainForm"/> as a
/// <see cref="NewDownloadResult"/>. Pre-fills from the clipboard the way a
/// download manager is expected to.
/// </summary>
public sealed class NewDownloadForm : Form
{
    private readonly ISettingsService _settingsService;
    private readonly IRemoteFileInfoProvider _fileInfoProvider = new RemoteFileInfoProvider();

    private TextBox _urlTextBox = null!;
    private Button _fetchButton = null!;
    private Label _statusLabel = null!;
    private TextBox _fileNameTextBox = null!;
    private TextBox _saveFolderTextBox = null!;
    private Label _fileTypeLabel = null!;
    private Label _fileSizeLabel = null!;
    private Label _rangeSupportLabel = null!;
    private NumericUpDown _connectionsUpDown = null!;
    private Button _okButton = null!;

    private RemoteFileInfo? _fetchedInfo;

    public NewDownloadResult? Result { get; private set; }

    /// <param name="initialUrl">
    /// A link to open the dialog on — supplied when QuickByte is launched with a
    /// URL, including a second launch handed over by <see cref="SingleInstance"/>.
    /// It wins over the clipboard: an explicit argument is a stronger signal
    /// about what the user wants than whatever they last copied.
    /// </param>
    public NewDownloadForm(ISettingsService settingsService, string? initialUrl = null)
    {
        _settingsService = settingsService;
        BuildUi();

        if (string.IsNullOrWhiteSpace(initialUrl))
        {
            PrefillFromClipboard();
            return;
        }

        _urlTextBox.Text = initialUrl.Trim();
        _statusLabel.Text = "Opened with a link — click Fetch Info to check it.";
    }

    private void BuildUi()
    {
        Text = "Add New Download";
        Width = 580;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BackColor = Theme.Surface;
        Font = Theme.Ui;
        Icon = BrandIcon.App;

        Controls.Add(BuildBody());
        Controls.Add(BuildFooter());
        Controls.Add(FormChrome.Header("Add New Download", "Paste a link, then choose where it lands.", IconFactory.AddUrl(40)));
    }

    private Panel BuildBody()
    {
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(24, 14, 24, 8) };

        // Row count and styles are declared up front: a TableLayoutPanel with an
        // unset RowCount re-flows explicitly positioned controls into the wrong
        // cells once a row is spanned.
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 7,
            BackColor = Theme.Surface,
            AutoSize = false
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

        // Address · status · info card · save as · save to · connections, then a
        // percent-sized filler — without it the last real row swallows all the
        // leftover height and its label floats out of line with its field.
        foreach (int height in new[] { 38, 26, 80, 38, 38, 38 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        int row = 0;

        // --- URL -------------------------------------------------------------
        layout.Controls.Add(FormChrome.FieldLabel("Address"), 0, row);
        _urlTextBox = FormChrome.Field();
        layout.Controls.Add(_urlTextBox, 1, row);
        _fetchButton = Theme.StyleButton(new Button { Text = "Fetch Info", Dock = DockStyle.Fill, Margin = new Padding(8, 4, 0, 4) }, primary: true);
        _fetchButton.Click += async (_, _) => await OnFetchClickedAsync();
        layout.Controls.Add(_fetchButton, 2, row++);

        // --- Status ----------------------------------------------------------
        _statusLabel = new Label
        {
            Text = "Enter a URL and press Enter or click Fetch Info.",
            Dock = DockStyle.Fill,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        layout.Controls.Add(_statusLabel, 1, row);
        layout.SetColumnSpan(_statusLabel, 2);
        row++;

        // --- Resolved file info card -----------------------------------------
        var infoCard = new Panel { Dock = DockStyle.Fill, BackColor = Theme.HeaderBack, Margin = new Padding(0, 6, 0, 10), Padding = new Padding(12, 6, 12, 6) };
        infoCard.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, infoCard.Width - 1, infoCard.Height - 1);
        };
        var infoGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, BackColor = Theme.HeaderBack };
        infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82));
        infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 52));
        infoGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        infoGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        infoGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        _fileTypeLabel = FormChrome.AddInfoCell(infoGrid, "Type:", 0, 0);
        _fileSizeLabel = FormChrome.AddInfoCell(infoGrid, "Size:", 2, 0);
        _rangeSupportLabel = FormChrome.AddInfoCell(infoGrid, "Resumable:", 0, 1, span: true);
        infoCard.Controls.Add(infoGrid);
        layout.Controls.Add(infoCard, 0, row);
        layout.SetColumnSpan(infoCard, 3);
        row++;

        // --- Save as ----------------------------------------------------------
        layout.Controls.Add(FormChrome.FieldLabel("Save as"), 0, row);
        _fileNameTextBox = FormChrome.Field();
        layout.Controls.Add(_fileNameTextBox, 1, row);
        layout.SetColumnSpan(_fileNameTextBox, 2);
        row++;

        // --- Save to ----------------------------------------------------------
        layout.Controls.Add(FormChrome.FieldLabel("Save to"), 0, row);
        _saveFolderTextBox = FormChrome.Field();
        _saveFolderTextBox.Text = _settingsService.Current.DefaultDownloadFolder;
        layout.Controls.Add(_saveFolderTextBox, 1, row);
        var browseButton = Theme.StyleButton(new Button { Text = "Browse…", Dock = DockStyle.Fill, Margin = new Padding(8, 4, 0, 4) });
        browseButton.Click += (_, _) => OnBrowseClicked();
        layout.Controls.Add(browseButton, 2, row++);

        // --- Connections ------------------------------------------------------
        layout.Controls.Add(FormChrome.FieldLabel("Connections"), 0, row);
        var connectionsRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Surface, WrapContents = false };
        _connectionsUpDown = new NumericUpDown
        {
            Minimum = DownloadSettings.MinConnections,
            Maximum = DownloadSettings.MaxConnections,
            Value = Math.Clamp(_settingsService.Current.DefaultConnectionsCount, DownloadSettings.MinConnections, DownloadSettings.MaxConnections),
            Width = 72,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Ui,
            Margin = new Padding(0, 5, 10, 0)
        };
        var connectionsHint = new Label
        {
            Text = $"parallel segments ({DownloadSettings.MinConnections}–{DownloadSettings.MaxConnections})",
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            Margin = new Padding(0, 9, 0, 0)
        };
        connectionsRow.Controls.Add(_connectionsUpDown);
        connectionsRow.Controls.Add(connectionsHint);
        layout.Controls.Add(connectionsRow, 1, row);
        layout.SetColumnSpan(connectionsRow, 2);

        body.Controls.Add(layout);
        return body;
    }

    private Panel BuildFooter()
    {
        var footer = FormChrome.Footer();
        var buttons = FormChrome.ButtonRow();

        _okButton = Theme.StyleButton(new Button { Text = "Download", Width = 104 }, primary: true);
        _okButton.Enabled = false;
        _okButton.Click += (_, _) => OnOkClicked();

        var cancelButton = Theme.StyleButton(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel });

        buttons.Controls.Add(_okButton);
        buttons.Controls.Add(cancelButton);
        footer.Controls.Add(buttons);

        AcceptButton = _okButton;
        CancelButton = cancelButton;
        return footer;
    }

    /// <summary>
    /// Enter in the address box fetches instead of activating the dialog's
    /// accept button. A single-line TextBox never sees Enter as a KeyDown when
    /// the form has an AcceptButton — it is swallowed by dialog-key handling —
    /// so the shortcut has to be claimed here.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter && _urlTextBox.Focused && _fetchButton.Enabled)
        {
            _ = OnFetchClickedAsync();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private void PrefillFromClipboard()
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            string text = Clipboard.GetText().Trim();
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                _urlTextBox.Text = text;
                _statusLabel.Text = "Pasted a link from the clipboard — click Fetch Info to check it.";
            }
        }
        catch { /* clipboard can be locked by another app */ }
    }

    private async Task OnFetchClickedAsync()
    {
        string url = _urlTextBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            SetStatus("Please enter a valid, absolute URL.", Theme.Danger);
            return;
        }

        _fetchButton.Enabled = false;
        _okButton.Enabled = false;
        SetStatus("Fetching file info…", Theme.TextMuted);

        try
        {
            _fetchedInfo = await _fileInfoProvider.GetFileInfoAsync(url);

            _fileNameTextBox.Text = _fetchedInfo.FileName;
            _fileTypeLabel.Text = _fetchedInfo.ContentType;
            _fileSizeLabel.Text = _fetchedInfo.HasKnownSize ? ByteFormatter.FormatBytes(_fetchedInfo.ContentLength) : "Unknown";
            _rangeSupportLabel.Text = _fetchedInfo.SupportsRangeRequests
                ? "Yes — multiple connections will be used"
                : "No — a single connection will be used";
            _rangeSupportLabel.ForeColor = _fetchedInfo.SupportsRangeRequests ? Theme.Success : Theme.Warning;

            _connectionsUpDown.Enabled = _fetchedInfo.SupportsRangeRequests;
            if (!_fetchedInfo.SupportsRangeRequests) _connectionsUpDown.Value = 1;

            SetStatus("File info retrieved successfully.", Theme.Success);
            _okButton.Enabled = true;
        }
        catch (Exception ex)
        {
            SetStatus($"Could not retrieve file info: {ex.Message}", Theme.Danger);
            _okButton.Enabled = false;
        }
        finally
        {
            _fetchButton.Enabled = true;
        }
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private void OnBrowseClicked()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = _saveFolderTextBox.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _saveFolderTextBox.Text = dialog.SelectedPath;
    }

    private void InitializeComponent()
    {

    }

    private void OnOkClicked()
    {
        if (_fetchedInfo is null)
        {
            SetStatus("Please fetch file info first.", Theme.Danger);
            return;
        }

        if (string.IsNullOrWhiteSpace(_fileNameTextBox.Text) || string.IsNullOrWhiteSpace(_saveFolderTextBox.Text))
        {
            SetStatus("File name and save folder are required.", Theme.Danger);
            return;
        }

        Result = new NewDownloadResult(
            _urlTextBox.Text.Trim(),
            _fetchedInfo,
            _saveFolderTextBox.Text.Trim(),
            _fileNameTextBox.Text.Trim(),
            (int)_connectionsUpDown.Value);

        DialogResult = DialogResult.OK;
        Close();
    }
}

/// <summary>Result payload handed back to <see cref="MainForm"/> when the user confirms a new download.</summary>
public sealed record NewDownloadResult(string Url, RemoteFileInfo FileInfo, string SaveFolder, string FileName, int ConnectionsCount);
