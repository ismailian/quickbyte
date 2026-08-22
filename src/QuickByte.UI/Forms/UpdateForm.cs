using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Forms;

/// <summary>
/// The window a found update goes through, in both directions it can be found
/// from. One window rather than two because the difference between the startup
/// check and Help &gt; Check for Updates is a single question — <em>does the
/// download start on its own?</em> — and everything after it (progress, the
/// integrity check, launching setup, what happens when the user cancels) is
/// identical.
///
/// <list type="bullet">
/// <item><see cref="UpdatePromptMode.Prompt"/> — the background check found
/// something. Nothing is fetched until the user asks for it: a download manager
/// that quietly spends the user's bandwidth on itself at every launch is the
/// thing this feature must not be.</item>
/// <item><see cref="UpdatePromptMode.Automatic"/> — the user went looking for
/// an update, so the download starts as the window opens and setup runs the
/// moment it lands.</item>
/// </list>
///
/// Returns <see cref="DialogResult.OK"/> only when the installer actually
/// started, which is the caller's cue to close QuickByte — setup cannot replace
/// files the running app holds open.
/// </summary>
public sealed class UpdateForm : Form
{
    private readonly IUpdateService _updateService;
    private readonly UpdateManifest _manifest;
    private readonly string _currentVersion;
    private readonly UpdatePromptMode _mode;

    private SmoothProgressBar _progressBar = null!;
    private Label _statusLabel = null!;
    private Panel _progressPanel = null!;
    private Button _updateButton = null!;
    private Button _laterButton = null!;

    private CancellationTokenSource? _cancellation;
    private bool _downloading;

    /// <summary>
    /// Set in FormClosed. The download is awaited on the UI thread, so its
    /// continuation can outlive the window by however long the socket takes to
    /// notice the cancellation — every post-await step checks this before
    /// touching a control.
    /// </summary>
    private bool _closed;

    public UpdateForm(IUpdateService updateService, UpdateManifest manifest, string currentVersion, UpdatePromptMode mode)
    {
        _updateService = updateService;
        _manifest = manifest;
        _currentVersion = currentVersion;
        _mode = mode;

        BuildUi();
    }

    // ---------------------------------------------------------------- UI --

    private void BuildUi()
    {
        bool hasNotes = !string.IsNullOrWhiteSpace(_manifest.ReleaseNotes);

        Text = "Update Available";
        Width = 540;
        // The notes box is the only variable-height thing in here; without notes
        // to show, the same height would leave a band of empty white.
        Height = hasNotes ? 428 : 320;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        Font = Theme.Ui;
        Icon = CreateIcon();

        Controls.Add(BuildBody(hasNotes));
        Controls.Add(BuildFooter());
        Controls.Add(FormChrome.Header(
            "Update available",
            $"QuickByte {_manifest.Version} is available to install",
            IconFactory.Update(32)));
    }

    private static Icon CreateIcon()
    {
        using var bmp = IconFactory.Update(32);
        return Icon.FromHandle(bmp.GetHicon());
    }

    private Panel BuildBody(bool hasNotes)
    {
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(22, 12, 22, 6) };

        // Docked children are laid out last-added-first, so the Fill control has
        // to go in before the edges it must not overlap.
        body.Controls.Add(hasNotes ? BuildNotes() : new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface });
        body.Controls.Add(BuildProgressPanel());
        body.Controls.Add(BuildVersionCard());
        return body;
    }

    private Panel BuildVersionCard()
    {
        var card = new Panel { Dock = DockStyle.Top, Height = 72, BackColor = Theme.HeaderBack, Padding = new Padding(14, 8, 14, 8) };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
        };

        // RowCount and RowStyles are declared before anything is added, and the
        // trailing filler row keeps the last real row from absorbing the slack.
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 3,
            BackColor = Theme.HeaderBack
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        AddCell(grid, "Installed:", _currentVersion, 0);
        AddCell(grid, "New version:", DescribeRelease(), 1);

        card.Controls.Add(grid);
        return card;
    }

    /// <summary>Version, plus whatever else the manifest bothered to say about it.</summary>
    private string DescribeRelease()
    {
        string text = _manifest.Version;
        if (_manifest.FileSizeBytes > 0) text += $"  ·  {ByteFormatter.FormatBytes(_manifest.FileSizeBytes)}";
        // Invariant, not current-culture: every other string in this window is
        // English (the assembly declares en-US and ships no resources), and a
        // localized month name inside an English sentence reads as a bug.
        if (_manifest.ReleaseDate is DateTimeOffset date)
            text += "  ·  released " + date.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        return text;
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
            Dock = DockStyle.Fill,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Margin = new Padding(0, 3, 4, 3)
        }, 1, row);
    }

    private Panel BuildNotes()
    {
        var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(0, 10, 0, 6) };

        // A read-only TextBox rather than a Label: release notes are written by
        // hand on the release side and can run to any length, and this is the
        // one control in the app that scrolls text without extra machinery.
        var notes = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = true,
            TabStop = false,
            ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.SurfaceAlt,
            ForeColor = Theme.Text,
            Font = Theme.UiSmall,
            Text = NormalizeNewlines(_manifest.ReleaseNotes!)
        };

        host.Controls.Add(notes);
        host.Controls.Add(new Label
        {
            Text = "What's new",
            Dock = DockStyle.Top,
            Height = 20,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmallBold
        });
        return host;
    }

    /// <summary>A TextBox renders a lone \n as a box, and JSON is full of them.</summary>
    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", Environment.NewLine);

    private Panel BuildProgressPanel()
    {
        _progressPanel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            BackColor = Theme.Surface,
            Visible = false
        };

        // Filled rather than docked to a fixed height: a failure hides the bar
        // and hands this label the whole panel, which is the difference between
        // reading the error and reading the first half of it.
        _statusLabel = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        _progressBar = new SmoothProgressBar { Dock = DockStyle.Top, Height = 20 };

        // Fill goes in first: docked children are laid out last-added-first.
        _progressPanel.Controls.Add(_statusLabel);
        _progressPanel.Controls.Add(_progressBar);
        return _progressPanel;
    }

    private Panel BuildFooter()
    {
        var footer = FormChrome.Footer();
        var buttons = FormChrome.ButtonRow();

        _updateButton = Theme.StyleButton(new Button { Text = "Update Now", Width = 108 }, primary: true);
        _updateButton.Click += (_, _) => StartDownload();

        // Closing is the cancel path in both directions: FormClosing cancels an
        // in-flight download, so this one button covers "not now" and "stop".
        _laterButton = Theme.StyleButton(new Button { Text = "Later", DialogResult = DialogResult.Cancel });
        _laterButton.Click += (_, _) => Close();

        buttons.Controls.Add(_updateButton);
        buttons.Controls.Add(_laterButton);
        footer.Controls.Add(buttons);

        AcceptButton = _updateButton;
        CancelButton = _laterButton;
        return footer;
    }

    // ----------------------------------------------------------- Behavior --

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_mode == UpdatePromptMode.Automatic) StartDownload();
    }

    private async void StartDownload()
    {
        if (_downloading) return;

        _downloading = true;
        _cancellation = new CancellationTokenSource();

        _updateButton.Enabled = false;
        _laterButton.Text = "Cancel";
        _progressPanel.Visible = true;
        _progressBar.Visible = true;
        _progressBar.SetValue(0, immediate: true);
        _progressBar.OverlayText = null;
        _statusLabel.AutoEllipsis = true;
        _statusLabel.ForeColor = Theme.TextMuted;
        _statusLabel.Text = "Connecting...";

        // Constructed on the UI thread so there is a context to capture at all —
        // the service reports from whatever thread the socket read completed on.
        var progress = new Progress<UpdateDownloadProgress>(ReportProgress);

        string installerPath;
        try
        {
            installerPath = await _updateService.DownloadInstallerAsync(_manifest, progress, _cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (!_closed) ResetAfterFailure("Update cancelled.", isError: false);
            return;
        }
        catch (Exception ex)
        {
            if (!_closed) ResetAfterFailure($"Download failed: {ex.Message}", isError: true);
            return;
        }
        finally
        {
            // Disposed here rather than on close: the token is still registered
            // inside HttpClient until the awaited call actually unwinds, and
            // disposing a source out from under it throws.
            _downloading = false;
            _cancellation?.Dispose();
            _cancellation = null;
        }

        if (_closed) return;
        LaunchInstaller(installerPath);
    }

    private void ReportProgress(UpdateDownloadProgress progress)
    {
        // Progress<T> marshals through whatever SynchronizationContext was
        // current where it was constructed — the WinForms one Program installs,
        // so in this app the callback already arrives on the UI thread. When
        // there is no context to capture it silently falls back to the thread
        // pool instead, and "the ambient context happened to be right" is not
        // an assumption this app makes anywhere else about touching a control.
        if (InvokeRequired)
        {
            if (IsHandleCreated && !_closed) BeginInvoke(() => ReportProgress(progress));
            return;
        }

        if (_closed) return;

        if (progress.Percentage is double percentage)
        {
            _progressBar.SetValue(percentage);
            _statusLabel.Text =
                $"Downloading  {ByteFormatter.FormatBytes(progress.BytesReceived)} of " +
                $"{ByteFormatter.FormatBytes(progress.TotalBytes)}  ·  " +
                $"{ByteFormatter.FormatSpeed(progress.SpeedBytesPerSecond)}";
            return;
        }

        // No Content-Length: there is no fraction to fill, so the bar carries the
        // byte count instead of pretending to know how far along it is.
        _progressBar.OverlayText = ByteFormatter.FormatBytes(progress.BytesReceived);
        _statusLabel.Text = $"Downloading  ·  {ByteFormatter.FormatSpeed(progress.SpeedBytesPerSecond)}";
    }

    private void LaunchInstaller(string installerPath)
    {
        _progressBar.SetValue(100);
        _progressBar.OverlayText = null;
        _statusLabel.Text = "Starting installer...";

        if (!FileLauncher.RunInstaller(installerPath))
        {
            ResetAfterFailure("The installer could not be started. It may have been blocked or the prompt was dismissed.", isError: true);
            return;
        }

        // The caller reads this as "close QuickByte" — setup cannot replace files
        // the running process holds open.
        DialogResult = DialogResult.OK;
        Close();
    }

    private void ResetAfterFailure(string message, bool isError)
    {
        _downloading = false;
        _progressBar.OverlayText = null;

        // A bar frozen part-way is no longer telling the truth about anything,
        // and the message — an HTTP or integrity failure, verbatim — needs the
        // room more than it does.
        _progressBar.Visible = false;
        _statusLabel.AutoEllipsis = false;
        _statusLabel.ForeColor = isError ? Theme.Danger : Theme.TextMuted;
        _statusLabel.Text = message;
        _updateButton.Text = "Try Again";
        _updateButton.Enabled = true;
        _laterButton.Text = "Close";
    }

    /// <summary>
    /// Cancels an in-flight download on the way out. Without this the socket
    /// keeps pulling an installer nobody is waiting for, and the temp file is
    /// left half-written.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _cancellation?.Cancel();
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closed = true;
        base.OnFormClosed(e);
    }
}

/// <summary>How the update window behaves once it is on screen.</summary>
public enum UpdatePromptMode
{
    /// <summary>Wait for the user to ask — what the startup check gets.</summary>
    Prompt,

    /// <summary>Download and run setup without further prompting — what a manual check gets.</summary>
    Automatic
}
