using System.Drawing;
using System.Windows.Forms;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Forms;

/// <summary>
/// "Add Download" dialog. Lets the user paste an http(s) or ftp(s) URL, fetches
/// file info (name, size, content type, range support) via
/// <see cref="IRemoteFileInfoProvider"/>, and lets them adjust the save
/// name/folder, the connection count (1–32) and the server login before handing
/// everything back to <see cref="MainForm"/> as a <see cref="DownloadRequest"/>.
/// Pre-fills from the clipboard the way a download manager is expected to.
///
/// The login fields are part of the *fetch*, not just of the download: a probe
/// that isn't authenticated resolves the size of a 401 body or an FTP error, so
/// credentials have to be in place before Fetch Info can say anything true.
/// </summary>
public sealed class NewDownloadForm : Form
{
    private readonly ISettingsService _settingsService;
    private readonly IRemoteFileInfoProvider _fileInfoProvider;

    /// <summary>
    /// Headers captured alongside a link handed over by the browser extension
    /// (cookie, referrer, user agent). Carried through the fetch and into the
    /// finished request unchanged — they are frequently the only reason the URL
    /// resolves to a file rather than a sign-in page.
    /// </summary>
    private readonly IReadOnlyDictionary<string, string>? _capturedHeaders;

    /// <summary>
    /// The file name Chrome already resolved for a captured download. It outranks
    /// whatever the fetch derives, because the browser followed the redirects and
    /// read the Content-Disposition that our probe may never see. Null for a URL
    /// the user typed, where each fetch is free to refresh the name.
    /// </summary>
    private readonly string? _preferredFileName;

    private TextBox _urlTextBox = null!;
    private Button _fetchButton = null!;
    private Label _statusLabel = null!;
    private CheckBox _useLoginCheckBox = null!;
    private TextBox _userNameTextBox = null!;
    private TextBox _passwordTextBox = null!;
    private TextBox _fileNameTextBox = null!;
    private TextBox _saveFolderTextBox = null!;
    private Label _fileTypeLabel = null!;
    private Label _fileSizeLabel = null!;
    private Label _rangeSupportLabel = null!;
    private NumericUpDown _connectionsUpDown = null!;
    private Button _okButton = null!;

    private RemoteFileInfo? _fetchedInfo;

    public DownloadRequest? Result { get; private set; }

    /// <param name="initialUrl">
    /// A link to open the dialog on — supplied when QuickByte is launched with a
    /// URL, including a second launch handed over by <see cref="SingleInstance"/>.
    /// It wins over the clipboard: an explicit argument is a stronger signal
    /// about what the user wants than whatever they last copied.
    /// </param>
    /// <param name="captured">
    /// A download the browser extension took over. Supplies the same URL plus
    /// Chrome's own file name, size and request headers, so the dialog opens
    /// already knowing what the browser knew.
    /// </param>
    public NewDownloadForm(
        ISettingsService settingsService,
        IRemoteFileInfoProvider fileInfoProvider,
        string? initialUrl = null,
        CapturedDownload? captured = null)
    {
        _settingsService = settingsService;
        _fileInfoProvider = fileInfoProvider;
        _capturedHeaders = captured?.ToHeaders();
        _preferredFileName = string.IsNullOrWhiteSpace(captured?.FileName)
            ? null
            : FileNameHelper.SanitizeFileName(captured!.FileName!);

        BuildUi();

        string? url = captured?.Url ?? initialUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            PrefillFromClipboard();
            return;
        }

        ApplySeed(url.Trim(), captured);
    }

    /// <summary>
    /// Fills the dialog from a link that arrived from outside — a command line, a
    /// second launch, or the browser extension — and starts the fetch without
    /// waiting to be asked. The user did not type this URL; making them click
    /// Fetch Info to confirm a link they already chose in the browser is a step
    /// with no decision in it.
    /// </summary>
    private void ApplySeed(string url, CapturedDownload? captured)
    {
        // A password embedded in the URL moves into the login fields rather than
        // staying in the address: DownloadItem.Url is persisted and displayed,
        // and the credential field is the only one that encrypts itself.
        var split = UrlCredentials.Extract(url);
        _urlTextBox.Text = split.Url;

        if (split.Credentials is not null)
        {
            _useLoginCheckBox.Checked = true;
            _userNameTextBox.Text = split.Credentials.UserName;
            _passwordTextBox.Text = split.Credentials.Password;
        }

        if (_preferredFileName is not null) _fileNameTextBox.Text = _preferredFileName;

        SetStatus("Checking the link…", Theme.TextMuted);
        _ = OnFetchClickedAsync();
    }

    private void BuildUi()
    {
        Text = "Add New Download";
        Width = 580;
        Height = 566;
        // Shown modeless and unowned by MainForm, so it is a window in its own
        // right: it gets a taskbar button, it can be minimised out of the way
        // while the fetch runs, and CenterParent would have no parent to centre
        // on. MainForm positions it.
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        BackColor = Theme.Surface;
        Font = Theme.Ui;
        Icon = BrandIcon.App;

        Controls.Add(BuildBody());
        Controls.Add(BuildFooter());
        Controls.Add(FormChrome.Header("Add New Download", "Paste an HTTP or FTP link, then choose where it lands.", IconFactory.AddUrl(40)));
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
            RowCount = 8,
            BackColor = Theme.Surface,
            AutoSize = false
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

        // Address · status · login · info card · save as · save to · connections,
        // then a percent-sized filler — without it the last real row swallows all
        // the leftover height and its label floats out of line with its field.
        foreach (int height in new[] { 38, 26, 34, 80, 38, 38, 38 })
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

        // --- Login ------------------------------------------------------------
        layout.Controls.Add(FormChrome.FieldLabel("Login"), 0, row);
        var loginRow = BuildLoginRow();
        layout.Controls.Add(loginRow, 1, row);
        layout.SetColumnSpan(loginRow, 2);
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

    private FlowLayoutPanel BuildLoginRow()
    {
        var loginRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Theme.Surface,
            WrapContents = false,
            Margin = new Padding(0, 2, 0, 0)
        };

        _useLoginCheckBox = new CheckBox
        {
            Text = "Sign in",
            AutoSize = true,
            Font = Theme.Ui,
            ForeColor = Theme.Text,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 5, 10, 0)
        };

        _userNameTextBox = LoginField("User name", 140);
        _passwordTextBox = LoginField("Password", 140);
        _passwordTextBox.UseSystemPasswordChar = true;

        _useLoginCheckBox.CheckedChanged += (_, _) =>
        {
            _userNameTextBox.Enabled = _passwordTextBox.Enabled = _useLoginCheckBox.Checked;
            if (_useLoginCheckBox.Checked) _userNameTextBox.Focus();
        };
        _userNameTextBox.Enabled = _passwordTextBox.Enabled = false;

        loginRow.Controls.Add(_useLoginCheckBox);
        loginRow.Controls.Add(_userNameTextBox);
        loginRow.Controls.Add(_passwordTextBox);
        return loginRow;
    }

    private static TextBox LoginField(string placeholder, int width)
    {
        var field = FormChrome.Field();
        field.Dock = DockStyle.None;
        field.Width = width;
        field.PlaceholderText = placeholder;
        field.Margin = new Padding(0, 3, 8, 0);
        return field;
    }

    private Panel BuildFooter()
    {
        var footer = FormChrome.Footer();
        var buttons = FormChrome.ButtonRow();

        _okButton = Theme.StyleButton(new Button { Text = "Download", Width = 104 }, primary: true);
        _okButton.Enabled = false;
        _okButton.Click += (_, _) => OnOkClicked();

        var cancelButton = Theme.StyleButton(new Button { Text = "Cancel", DialogResult = DialogResult.Cancel });
        // Closed explicitly: a DialogResult only closes the form by itself while
        // it is modal, and this one is not.
        cancelButton.Click += (_, _) => Close();

        buttons.Controls.Add(_okButton);
        buttons.Controls.Add(cancelButton);
        footer.Controls.Add(buttons);

        AcceptButton = _okButton;
        CancelButton = cancelButton;
        return footer;
    }

    /// <summary>
    /// Enter in the address or login boxes fetches instead of activating the
    /// dialog's accept button. A single-line TextBox never sees Enter as a
    /// KeyDown when the form has an AcceptButton — it is swallowed by dialog-key
    /// handling — so the shortcut has to be claimed here.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        bool inFetchField = _urlTextBox.Focused || _userNameTextBox.Focused || _passwordTextBox.Focused;
        if (keyData == Keys.Enter && inFetchField && _fetchButton.Enabled)
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
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && IsSupportedScheme(uri))
            {
                _urlTextBox.Text = text;
                _statusLabel.Text = "Pasted a link from the clipboard — click Fetch Info to check it.";
            }
        }
        catch { /* clipboard can be locked by another app */ }
    }

    private static bool IsSupportedScheme(Uri uri) =>
        uri.Scheme is "http" or "https" or "ftp" or "ftps";

    private RequestOptions CurrentRequestOptions() => new()
    {
        Credentials = CurrentCredentials(),
        Headers = _capturedHeaders
    };

    private DownloadCredentials? CurrentCredentials()
    {
        if (!_useLoginCheckBox.Checked) return null;

        var credentials = new DownloadCredentials
        {
            UserName = _userNameTextBox.Text.Trim(),
            Password = _passwordTextBox.Text
        };
        return credentials.IsEmpty ? null : credentials;
    }

    private async Task OnFetchClickedAsync()
    {
        // A URL typed with credentials in it is split here too, not just on the
        // seeded path — people paste ftp://user:pass@host links by hand.
        var split = UrlCredentials.Extract(_urlTextBox.Text.Trim());
        if (split.Credentials is not null)
        {
            _urlTextBox.Text = split.Url;
            _useLoginCheckBox.Checked = true;
            _userNameTextBox.Text = split.Credentials.UserName;
            _passwordTextBox.Text = split.Credentials.Password;
        }

        string url = split.Url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsSupportedScheme(uri))
        {
            SetStatus("Please enter a valid http, https, ftp or ftps URL.", Theme.Danger);
            return;
        }

        _fetchButton.Enabled = false;
        _okButton.Enabled = false;
        SetStatus("Fetching file info…", Theme.TextMuted);

        try
        {
            var info = await _fileInfoProvider.GetFileInfoAsync(url, CurrentRequestOptions());

            // The seeded path starts this fetch from the constructor, so a user
            // who cancels while "Checking the link..." is on screen resumes here
            // against a disposed form. Every continuation below checks first.
            if (IsDisposed || Disposing) return;
            _fetchedInfo = info;

            _fileNameTextBox.Text = _preferredFileName ?? _fetchedInfo.FileName;

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
        catch (AuthenticationRequiredException ex)
        {
            // The one failure with an obvious next step, so it gets one: the login
            // fields open and take focus instead of the user reading a generic
            // "could not retrieve file info" and guessing.
            if (IsDisposed || Disposing) return;
            OnAuthenticationRequired(ex);
        }
        catch (Exception ex)
        {
            if (IsDisposed || Disposing) return;
            SetStatus($"Could not retrieve file info: {ex.Message}", Theme.Danger);
            _okButton.Enabled = false;
        }
        finally
        {
            if (!IsDisposed && !Disposing) _fetchButton.Enabled = true;
        }
    }

    private void OnAuthenticationRequired(AuthenticationRequiredException exception)
    {
        _fetchedInfo = null;
        _okButton.Enabled = false;
        _useLoginCheckBox.Checked = true;

        SetStatus($"{exception.Message} Enter it below and fetch again.", Theme.Warning);

        var focusTarget = exception.CredentialsWereSupplied && _userNameTextBox.Text.Length > 0
            ? _passwordTextBox
            : _userNameTextBox;
        focusTarget.Focus();
        focusTarget.SelectAll();
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

        Result = new DownloadRequest(
            _urlTextBox.Text.Trim(),
            _fetchedInfo,
            _saveFolderTextBox.Text.Trim(),
            _fileNameTextBox.Text.Trim(),
            (int)_connectionsUpDown.Value)
        {
            Credentials = CurrentCredentials(),
            Headers = _capturedHeaders
        };

        DialogResult = DialogResult.OK;
        Close();
    }
}
