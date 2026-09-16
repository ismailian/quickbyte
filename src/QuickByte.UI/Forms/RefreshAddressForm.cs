using System.Drawing;
using System.Windows.Forms;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Forms;

/// <summary>
/// "Refresh Download Address": gives a download a new link without losing the
/// bytes it already fetched.
///
/// The link is very often the only thing wrong with a failed download. Signed
/// URLs — S3, Google Drive, the download button on most sites that sit behind a
/// login or a CDN — expire after minutes or hours, so a download paused
/// overnight resumes into a <c>403</c> while gigabytes of it are sitting on disk.
/// Removing it and adding it again would start from zero for a file that has not
/// changed.
///
/// There are two ways in, because an expired link usually cannot just be pasted:
/// the page that issued it has to issue a fresh one. So besides the address box,
/// this window listens for the browser — click the download link on that page
/// again, and <see cref="MainForm.HandleCapturedDownload"/> hands the capture
/// here instead of opening an Add window (see
/// <see cref="AddressRefresh.MatchesCapture"/>). The capture brings the page's
/// fresh cookie along with it, which is frequently what made the old link stop
/// working in the first place.
///
/// Whatever arrives is probed and judged by <see cref="AddressRefresh.Judge"/>
/// before anything changes: a link to a file of a different size is refused, and
/// one to a new version of the file can only start the download over.
/// </summary>
public sealed class RefreshAddressForm : Form
{
    private readonly IDownloadManager _downloadManager;
    private readonly IRemoteFileInfoProvider _fileInfoProvider;

    private TextBox _newUrlTextBox = null!;
    private Button _checkButton = null!;
    private Label _statusLabel = null!;
    private CheckBox _resumeCheckBox = null!;
    private Button _updateButton = null!;

    /// <summary>Headers that came with a browser capture; null keeps the download's own.</summary>
    private IReadOnlyDictionary<string, string>? _capturedHeaders;

    /// <summary>A login typed into <see cref="CredentialsForm"/> or lifted out of the URL; null keeps the download's own.</summary>
    private DownloadCredentials? _credentials;

    private Candidate? _candidate;
    private bool _checking;

    public RefreshAddressForm(DownloadItem item, IDownloadManager downloadManager, IRemoteFileInfoProvider fileInfoProvider)
    {
        Item = item;
        _downloadManager = downloadManager;
        _fileInfoProvider = fileInfoProvider;

        BuildUi();
        PrefillFromClipboard();
    }

    public DownloadItem Item { get; }

    /// <summary>
    /// Whether a browser capture may be routed here. Not while a check is running
    /// — the capture would be answered by the result of the link it replaced.
    /// </summary>
    public bool IsWaitingForCapture => !IsDisposed && !Disposing && !_checking;

    /// <summary>A link the browser handed over while this window was open. Checked straight away.</summary>
    public void AcceptCapture(CapturedDownload captured)
    {
        _capturedHeaders = captured.ToHeaders();
        _newUrlTextBox.Text = captured.Url;
        _ = CheckAsync();
    }

    private void BuildUi()
    {
        Text = "Refresh Download Address";
        Width = 600;
        Height = 432;
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
        Controls.Add(FormChrome.Header("Refresh Download Address", Item.FileName, IconFactory.RefreshAddress(40)));
    }

    private Panel BuildBody()
    {
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(24, 12, 24, 8) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 7, BackColor = Theme.Surface };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));

        // Current · new · status · explanation · open page · resume, then the filler.
        foreach (int height in new[] { 38, 38, 44, 50, 26, 30 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        int row = 0;

        layout.Controls.Add(FormChrome.FieldLabel("Current"), 0, row);
        var current = FormChrome.Field();
        current.ReadOnly = true;
        // Out of the tab order, or it takes focus first and scrolls to the end
        // of a signed URL — the one part of it that says nothing.
        current.TabStop = false;
        current.BackColor = Theme.HeaderBack;
        current.ForeColor = Theme.TextMuted;
        current.Text = Item.Url;
        layout.Controls.Add(current, 1, row);
        layout.SetColumnSpan(current, 2);
        row++;

        layout.Controls.Add(FormChrome.FieldLabel("New address"), 0, row);
        _newUrlTextBox = FormChrome.Field();
        _newUrlTextBox.TextChanged += (_, _) => InvalidateCandidate();
        layout.Controls.Add(_newUrlTextBox, 1, row);
        _checkButton = Theme.StyleButton(new Button { Text = "Check", Dock = DockStyle.Fill, Margin = new Padding(8, 4, 0, 4) }, primary: true);
        _checkButton.Click += async (_, _) => await CheckAsync();
        layout.Controls.Add(_checkButton, 2, row++);

        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        SetStatus(ProgressSummary(), Theme.TextMuted);
        layout.Controls.Add(_statusLabel, 1, row);
        layout.SetColumnSpan(_statusLabel, 2);
        row++;

        var explanation = new Label
        {
            Text = "Paste the new link, or click the download link again in your browser — " +
                   "QuickByte catches it here rather than starting a new download.",
            Dock = DockStyle.Fill,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall
        };
        layout.Controls.Add(explanation, 1, row);
        layout.SetColumnSpan(explanation, 2);
        row++;

        // Offered only when the download came from a page the browser told us
        // about: that page is where a fresh link comes from.
        if (PageUrl() is { } page)
        {
            var openPage = new LinkLabel
            {
                Text = "Open the page this download came from",
                Dock = DockStyle.Fill,
                Font = Theme.UiSmall,
                LinkColor = Theme.Accent,
                ActiveLinkColor = Theme.AccentDark,
                TextAlign = ContentAlignment.MiddleLeft
            };
            openPage.LinkClicked += (_, _) => FileLauncher.OpenWebPage(page);
            layout.Controls.Add(openPage, 1, row);
            layout.SetColumnSpan(openPage, 2);
        }
        row++;

        _resumeCheckBox = new CheckBox
        {
            Text = "Resume the download once the address is updated",
            Checked = true,
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Font = Theme.Ui
        };
        layout.Controls.Add(_resumeCheckBox, 1, row);
        layout.SetColumnSpan(_resumeCheckBox, 2);

        body.Controls.Add(layout);
        return body;
    }

    private Panel BuildFooter()
    {
        var footer = FormChrome.Footer();
        var buttons = FormChrome.ButtonRow();

        _updateButton = Theme.StyleButton(new Button { Text = "Update", Width = 104, Enabled = false }, primary: true);
        _updateButton.Click += (_, _) => OnUpdateClicked();

        var cancelButton = Theme.StyleButton(new Button { Text = "Cancel" });
        // Modeless: a DialogResult alone would not close it.
        cancelButton.Click += (_, _) => Close();

        buttons.Controls.Add(_updateButton);
        buttons.Controls.Add(cancelButton);
        footer.Controls.Add(buttons);

        CancelButton = cancelButton;
        return footer;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _newUrlTextBox.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Enter && _newUrlTextBox.Focused && _checkButton.Enabled)
        {
            _ = CheckAsync();
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

            // Only a link that is not the one already failing: pre-filling the
            // dead address would invite checking it.
            if (IsSupportedUrl(text) && !string.Equals(text, Item.Url, StringComparison.Ordinal))
                _newUrlTextBox.Text = text;
        }
        catch { /* clipboard can be locked by another app */ }
    }

    private static bool IsSupportedUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp" or "ftps";

    private string? PageUrl()
    {
        foreach (var (name, value) in Item.Headers)
        {
            if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https")
                return uri.AbsoluteUri;
        }
        return null;
    }

    private string ProgressSummary() =>
        AddressRefresh.HasProgress(Item)
            ? $"{ByteFormatter.FormatBytes(Item.DownloadedBytes)} of {ByteFormatter.FormatBytes(Item.TotalBytes)} is already downloaded and will be kept."
            : "Nothing has been downloaded yet.";

    private void InvalidateCandidate()
    {
        if (_checking) return;
        _candidate = null;
        _updateButton.Enabled = false;
        _updateButton.Text = "Update";
    }

    private async Task CheckAsync()
    {
        var split = UrlCredentials.Extract(_newUrlTextBox.Text.Trim());
        if (split.Credentials is not null)
        {
            _credentials = split.Credentials;
            _newUrlTextBox.Text = split.Url;
        }

        if (!IsSupportedUrl(split.Url))
        {
            SetStatus("Enter a valid http, https, ftp or ftps address.", Theme.Danger);
            return;
        }

        _checking = true;
        _candidate = null;
        _checkButton.Enabled = false;
        _updateButton.Enabled = false;
        _updateButton.Text = "Update";
        SetStatus("Checking the new address…", Theme.TextMuted);

        try
        {
            // Asked the way the download itself will ask: its own login and
            // headers, unless the new link brought its own.
            var options = new RequestOptions
            {
                Credentials = _credentials ?? Item.Credentials,
                Headers = _capturedHeaders ?? (Item.Headers.Count > 0 ? Item.Headers : null)
            };
            var info = await _fileInfoProvider.GetFileInfoAsync(split.Url, options);
            if (IsDisposed || Disposing) return;

            ShowVerdict(new Candidate(split.Url, info, AddressRefresh.Judge(Item, info)));
        }
        catch (AuthenticationRequiredException ex)
        {
            if (IsDisposed || Disposing) return;
            _checking = false;
            await OnAuthenticationRequiredAsync(split.Url, ex).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (IsDisposed || Disposing) return;
            SetStatus($"Could not check the new address: {ex.Message}", Theme.Danger);
        }
        finally
        {
            if (!IsDisposed && !Disposing)
            {
                _checking = false;
                _checkButton.Enabled = true;
            }
        }
    }

    /// <summary>
    /// The new link asks for a login. Also how a download whose password was
    /// changed gets the new one: there is nowhere else to type it.
    /// </summary>
    private async Task OnAuthenticationRequiredAsync(string url, AuthenticationRequiredException exception)
    {
        SetStatus(exception.Message, Theme.Warning);

        DownloadCredentials? supplied;
        using (var prompt = new CredentialsForm(url, exception.Message, _credentials ?? Item.Credentials))
            supplied = prompt.ShowDialog(this) == DialogResult.OK ? prompt.Result : null;

        if (supplied is null || IsDisposed || Disposing) return;

        _credentials = supplied;
        await CheckAsync().ConfigureAwait(true);
    }

    private void ShowVerdict(Candidate candidate)
    {
        string size = candidate.Info.HasKnownSize ? ByteFormatter.FormatBytes(candidate.Info.ContentLength) : "unknown size";

        switch (candidate.Verdict)
        {
            case AddressVerdict.DifferentSize:
                SetStatus(
                    $"This address points to a different file ({size}, not {ByteFormatter.FormatBytes(Item.TotalBytes)}). " +
                    "Add it as a new download instead.", Theme.Danger);
                return;

            case AddressVerdict.NewVersion:
                SetStatus("The file at this address has changed since the download started. Updating starts it over.", Theme.Warning);
                _updateButton.Text = "Start Over";
                break;

            case AddressVerdict.Unverifiable:
                SetStatus("The server did not say how big the file is, so it cannot be confirmed as the same file.", Theme.Warning);
                break;

            default:
                SetStatus($"Same file ({size}). {ProgressSummary()}", Theme.Success);
                _updateButton.Text = "Update";
                break;
        }

        _candidate = candidate;
        _updateButton.Enabled = true;
        AcceptButton = _updateButton;
    }

    private void OnUpdateClicked()
    {
        if (_candidate is null) return;

        bool discard = _candidate.Verdict == AddressVerdict.NewVersion;

        if (_candidate.Verdict == AddressVerdict.Unverifiable)
        {
            // The user may well know it is the same file — a mirror of an ISO
            // they recognise. Keeping the bytes is their call, not ours, but
            // the question has to say what the risk is.
            var answer = MessageBox.Show(this,
                "QuickByte cannot confirm that the new address serves the same file.\n\n" +
                "Yes — keep what is downloaded and continue (a different file would be corrupted).\n" +
                "No — start the download over from the new address.",
                Text, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);

            if (answer == DialogResult.Cancel) return;
            discard = answer == DialogResult.No;
        }

        var change = new AddressChange(_candidate.Url, _candidate.Info)
        {
            Credentials = _credentials,
            Headers = _capturedHeaders,
            DiscardProgress = discard
        };

        if (!_downloadManager.ChangeAddress(Item.Id, change))
        {
            SetStatus("The download is running. Pause it, then update the address.", Theme.Danger);
            return;
        }

        if (_resumeCheckBox.Checked)
            _ = _downloadManager.ResumeAsync(Item.Id);

        Close();
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    private sealed record Candidate(string Url, RemoteFileInfo Info, AddressVerdict Verdict);
}
