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
/// name/folder and the connection count before handing everything back to
/// <see cref="MainForm"/> as a <see cref="DownloadRequest"/>. Pre-fills from the
/// clipboard the way a download manager is expected to.
///
/// There are no login fields on this window. Credentials are part of the
/// *fetch* — a probe that isn't authenticated resolves the size of a 401 body or
/// an FTP error — but almost no download needs any, and a checkbox and two boxes
/// sitting on every one of them made the ordinary case look like it had a
/// decision in it. They are asked for when a server actually asks:
/// <see cref="CredentialsForm"/> opens on an
/// <see cref="AuthenticationRequiredException"/> and the fetch is retried with
/// what it collected. See <see cref="OnAuthenticationRequiredAsync"/>.
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
    private TextBox _fileNameTextBox = null!;
    private TextBox _saveFolderTextBox = null!;
    private Label _fileTypeLabel = null!;
    private Label _fileSizeLabel = null!;
    private Label _rangeSupportLabel = null!;
    private ConnectionCountBox _connectionsBox = null!;
    private ComboBox _queueComboBox = null!;
    private Button _okButton = null!;

    private RemoteFileInfo? _fetchedInfo;

    /// <summary>
    /// The login this dialog has been given — from a <c>user:pass@host</c> URL,
    /// or from <see cref="CredentialsForm"/> after the server asked for one. Null
    /// for the overwhelming majority of downloads, which is why it has no fields
    /// on screen. It lives here rather than being read back off controls because
    /// the controls are gone, and because it has to reach the
    /// <see cref="DownloadRequest"/>: that is what lets a resume days later
    /// present the same login.
    /// </summary>
    private DownloadCredentials? _credentials;

    /// <summary>
    /// The queues this download may be filed into. Passed in rather than pulled
    /// from a manager: this dialog resolves a link, it does not own queue state,
    /// and the caller is the one that puts the finished download into the queue.
    /// </summary>
    private readonly IReadOnlyList<DownloadQueue> _queues;

    public DownloadRequest? Result { get; private set; }

    /// <summary>
    /// The queue the user chose, or null for "start now". When it is set,
    /// <see cref="Result"/> asks for a download that is added but not started —
    /// the queue decides when it runs.
    /// </summary>
    public Guid? SelectedQueueId { get; private set; }

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
        CapturedDownload? captured = null,
        IReadOnlyList<DownloadQueue>? queues = null)
    {
        _settingsService = settingsService;
        _fileInfoProvider = fileInfoProvider;
        _queues = queues ?? Array.Empty<DownloadQueue>();
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
        // A password embedded in the URL is lifted out of the address rather
        // than left in it: DownloadItem.Url is persisted, displayed and put in
        // tooltips, and the credential field is the only one that encrypts
        // itself.
        var split = UrlCredentials.Extract(url);
        _urlTextBox.Text = split.Url;
        if (split.Credentials is not null) _credentials = split.Credentials;

        if (_preferredFileName is not null) _fileNameTextBox.Text = _preferredFileName;

        SetStatus("Checking the link…", Theme.TextMuted);
        _ = OnFetchClickedAsync();
    }

    private void BuildUi()
    {
        Text = "Add New Download";
        Width = 580;
        // 34 px shorter than it was: the login row it used to carry is a dialog
        // of its own now, shown only when a server asks for a login.
        Height = 572;
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

        // Address · status · info card · save as · save to · connections · queue,
        // then a percent-sized filler — without it the last real row swallows all
        // the leftover height and its label floats out of line with its field.
        foreach (int height in new[] { 38, 26, 80, 38, 38, 38, 38 })
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

        // Shown as ~\Downloads\QuickByte rather than the full path. The box is
        // narrower than a real profile path, and the half that would be clipped
        // is the half that says where the file is going. UserPath.Expand puts it
        // back before anything creates a directory from it.
        _saveFolderTextBox.Text = UserPath.Shorten(_settingsService.Current.DefaultDownloadFolder);
        layout.Controls.Add(_saveFolderTextBox, 1, row);
        var browseButton = Theme.StyleButton(new Button { Text = "Browse…", Dock = DockStyle.Fill, Margin = new Padding(8, 4, 0, 4) });
        browseButton.Click += (_, _) => OnBrowseClicked();
        layout.Controls.Add(browseButton, 2, row++);

        // --- Connections ------------------------------------------------------
        layout.Controls.Add(FormChrome.FieldLabel("Connections"), 0, row);
        var connectionsRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Surface, WrapContents = false };
        _connectionsBox = new ConnectionCountBox
        {
            Width = 72,
            Margin = new Padding(0, 6, 10, 0),
            Connections = _settingsService.Current.DefaultConnectionsCount
        };
        var connectionsHint = new Label
        {
            Text = "parallel segments",
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            Margin = new Padding(0, 9, 0, 0)
        };
        connectionsRow.Controls.Add(_connectionsBox);
        connectionsRow.Controls.Add(connectionsHint);
        layout.Controls.Add(connectionsRow, 1, row);
        layout.SetColumnSpan(connectionsRow, 2);
        row++;

        // --- Queue ------------------------------------------------------------
        layout.Controls.Add(FormChrome.FieldLabel("Queue"), 0, row);
        _queueComboBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
            Margin = new Padding(0, 6, 0, 6)
        };

        // "Start now" is the first entry and the default, because it is what the
        // Download button has always meant. Choosing a queue instead is the
        // deliberate act, and it is the one that leaves the file waiting.
        _queueComboBox.Items.Add(new QueueChoice(null, "Start now — don't queue it"));
        foreach (var queue in _queues)
            _queueComboBox.Items.Add(new QueueChoice(queue.Id, $"Add to \"{queue.Name}\""));
        _queueComboBox.SelectedIndex = 0;
        _queueComboBox.Enabled = _queues.Count > 0;

        layout.Controls.Add(_queueComboBox, 1, row);
        layout.SetColumnSpan(_queueComboBox, 2);

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
        Credentials = _credentials,
        Headers = _capturedHeaders
    };

    private async Task OnFetchClickedAsync()
    {
        // A URL typed with credentials in it is split here too, not just on the
        // seeded path — people paste ftp://user:pass@host links by hand. The
        // password moves out of the address because DownloadItem.Url is
        // persisted, displayed and put in tooltips.
        var split = UrlCredentials.Extract(_urlTextBox.Text.Trim());
        if (split.Credentials is not null)
        {
            _urlTextBox.Text = split.Url;
            _credentials = split.Credentials;
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

            _connectionsBox.Enabled = _fetchedInfo.SupportsRangeRequests;
            if (!_fetchedInfo.SupportsRangeRequests) _connectionsBox.Connections = 1;

            SetStatus("File info retrieved successfully.", Theme.Success);
            _okButton.Enabled = true;
        }
        catch (AuthenticationRequiredException ex)
        {
            // The one failure with an obvious next step, so it gets one: a
            // prompt for the login, rather than a generic "could not retrieve
            // file info" the user has to interpret.
            if (IsDisposed || Disposing) return;
            await OnAuthenticationRequiredAsync(ex).ConfigureAwait(true);
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

    /// <summary>
    /// The server asked for a login. Puts <see cref="CredentialsForm"/> up with
    /// what it said, and — if the user answers it — fetches again straight
    /// away, because the only reason anyone typed a password into that window
    /// was to get past this.
    ///
    /// Modal on this window rather than another independent one: there is
    /// nothing useful to do in the Add dialog until the question is answered,
    /// and a modeless prompt would let a second Fetch run behind it. Several Add
    /// windows can still be open at once, each with its own prompt, which is
    /// exactly right when two browser captures come from two different servers.
    ///
    /// A rejected password comes back through here again, since the retried
    /// fetch raises the same exception with a different message — that loop is
    /// the user's to break, and Cancel is how they break it.
    /// </summary>
    private async Task OnAuthenticationRequiredAsync(AuthenticationRequiredException exception)
    {
        _fetchedInfo = null;
        _okButton.Enabled = false;
        SetStatus(exception.Message, Theme.Warning);

        DownloadCredentials? supplied;
        using (var prompt = new CredentialsForm(_urlTextBox.Text.Trim(), exception.Message, _credentials))
        {
            // ShowDialog disables this window while it is up, which is what
            // stops a second fetch being started behind it — the prompt is the
            // only thing that can re-enter OnFetchClickedAsync, and it does so
            // below, after it has closed.
            supplied = prompt.ShowDialog(this) == DialogResult.OK ? prompt.Result : null;
        }

        // Cancelled. The status line already says what the server wanted, and
        // Fetch Info is there for a second try.
        if (supplied is null)
        {
            SetStatus($"{exception.Message} Click Fetch Info to try again.", Theme.Warning);
            return;
        }

        _credentials = supplied;

        // The prompt was pumping messages, so the window may have been closed
        // underneath it — every continuation in this class checks.
        if (IsDisposed || Disposing) return;

        await OnFetchClickedAsync().ConfigureAwait(true);
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private void OnBrowseClicked()
    {
        // Expanded on the way in — a FolderBrowserDialog handed "~\Downloads"
        // opens on nothing — and shortened again on the way back out, so
        // browsing to the profile does not undo the abbreviation.
        using var dialog = new FolderBrowserDialog { SelectedPath = UserPath.Expand(_saveFolderTextBox.Text) };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _saveFolderTextBox.Text = UserPath.Shorten(dialog.SelectedPath);
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

        SelectedQueueId = (_queueComboBox.SelectedItem as QueueChoice)?.QueueId;

        Result = new DownloadRequest(
            _urlTextBox.Text.Trim(),
            _fetchedInfo,
            UserPath.Expand(_saveFolderTextBox.Text),
            _fileNameTextBox.Text.Trim(),
            _connectionsBox.Connections)
        {
            Credentials = _credentials,
            Headers = _capturedHeaders,
            StartImmediately = SelectedQueueId is null
        };

        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>One entry in the queue drop-down; the text is what the list draws.</summary>
    private sealed class QueueChoice
    {
        public QueueChoice(Guid? queueId, string label)
        {
            QueueId = queueId;
            Label = label;
        }

        public Guid? QueueId { get; }

        private string Label { get; }

        public override string ToString() => Label;
    }
}
