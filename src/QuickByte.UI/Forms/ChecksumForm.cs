using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Forms;

/// <summary>
/// Checks a finished download against a checksum given after the fact — the
/// publisher's page is often read only once the file is already on disk.
///
/// The check runs through <see cref="IDownloadManager.VerifyChecksumAsync"/>, so
/// its result is kept with the download and shown in the list, and it can be
/// cancelled: hashing a 40 GB file takes a while.
/// </summary>
public sealed class ChecksumForm : Form
{
    private readonly DownloadItem _item;
    private readonly IDownloadManager _downloadManager;
    private CancellationTokenSource? _running;

    private TextBox _checksumTextBox = null!;
    private Label _statusLabel = null!;
    private SmoothProgressBar _progressBar = null!;
    private Button _verifyButton = null!;

    public ChecksumForm(DownloadItem item, IDownloadManager downloadManager)
    {
        _item = item;
        _downloadManager = downloadManager;

        BuildUi();

        // A checksum the download was added with is the one worth checking again.
        if (FileChecksum.FromStored(item.ExpectedChecksum) is { } stored)
            _checksumTextBox.Text = stored.Hex;
        else
            PrefillFromClipboard();

        FormClosed += (_, _) => _running?.Cancel();

        // Last, once every control exists: see Theme.Attach.
        Theme.Attach(this);
    }

    public DownloadItem Item => _item;

    private void BuildUi()
    {
        Text = "Verify Checksum";
        Width = 580;
        Height = 330;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Surface;
        Font = Theme.Ui;
        Icon = BrandIcon.App;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(24, 14, 24, 8), BackColor = Theme.Surface };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, BackColor = Theme.Surface };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(FormChrome.FieldLabel("Checksum"), 0, 0);
        _checksumTextBox = FormChrome.Field();
        _checksumTextBox.Font = Theme.Mono;
        _checksumTextBox.PlaceholderText = "SHA-256, SHA-512, SHA-1 or MD5";
        layout.Controls.Add(_checksumTextBox, 1, 0);

        _statusLabel = new Label
        {
            Text = "Paste the checksum from the download page. The file is read once, start to finish.",
            Dock = DockStyle.Fill,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall
        };
        layout.Controls.Add(_statusLabel, 1, 1);

        _progressBar = new SmoothProgressBar { Dock = DockStyle.Fill, BarColor = Theme.Accent, Margin = new Padding(0, 4, 0, 4), Visible = false };
        layout.Controls.Add(_progressBar, 1, 2);

        body.Controls.Add(layout);

        var footer = FormChrome.Footer();
        var buttons = FormChrome.ButtonRow();
        _verifyButton = Theme.StyleButton(new Button { Text = "Verify", Width = 96 }, primary: true);
        _verifyButton.Click += async (_, _) => await VerifyAsync();
        var close = Theme.StyleButton(new Button { Text = "Close" });
        close.Click += (_, _) => Close();
        buttons.Controls.Add(_verifyButton);
        buttons.Controls.Add(close);
        footer.Controls.Add(buttons);
        AcceptButton = _verifyButton;
        CancelButton = close;

        Controls.Add(body);
        Controls.Add(footer);
        Controls.Add(FormChrome.Header("Verify Checksum", _item.FileName, IconFactory.CheckCircle(40)));
    }

    private void PrefillFromClipboard()
    {
        try
        {
            if (Clipboard.ContainsText() && FileChecksum.Parse(Clipboard.GetText()) is { } found)
                _checksumTextBox.Text = found.Hex;
        }
        catch { /* clipboard can be locked by another app */ }
    }

    private async Task VerifyAsync()
    {
        if (_running is not null)
        {
            _running.Cancel();
            return;
        }

        if (FileChecksum.Parse(_checksumTextBox.Text) is not { } expected)
        {
            SetStatus("That is not a checksum QuickByte recognises. Paste the hex value from the download page.", Theme.Danger);
            return;
        }

        using var running = new CancellationTokenSource();
        _running = running;
        _verifyButton.Text = "Cancel";
        _checksumTextBox.Enabled = false;
        _progressBar.Visible = true;
        _progressBar.SetValue(0, immediate: true);
        SetStatus($"Checking the {FileChecksum.Name(expected.Algorithm)}…", Theme.TextMuted);

        long size = Math.Max(1, _item.TotalBytes);
        var progress = new Progress<long>(read =>
        {
            if (!IsDisposed) _progressBar.SetValue(Math.Min(100, read * 100.0 / size));
        });

        try
        {
            var result = await _downloadManager.VerifyChecksumAsync(_item.Id, expected, progress, running.Token);
            if (IsDisposed) return;

            _progressBar.SetValue(100, immediate: true);
            switch (result)
            {
                case ChecksumResult.Match:
                    _progressBar.BarColor = Theme.Success;
                    SetStatus(FileChecksum.IsWeak(expected.Algorithm)
                            ? $"The {FileChecksum.Name(expected.Algorithm)} matches — the file arrived intact. " +
                              "This hash cannot rule out a file forged to match it."
                            : $"The {FileChecksum.Name(expected.Algorithm)} matches. This is the file that was published.",
                        Theme.Success);
                    break;
                case ChecksumResult.Mismatch:
                    _progressBar.BarColor = Theme.Danger;
                    SetStatus($"The {FileChecksum.Name(expected.Algorithm)} does not match. The file is damaged or not the one published — don't open it.",
                        Theme.Danger);
                    break;
                default:
                    SetStatus("The file is no longer where it was saved.", Theme.Danger);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed) SetStatus("Cancelled.", Theme.TextMuted);
        }
        catch (Exception ex)
        {
            if (!IsDisposed) SetStatus($"Could not read the file: {ex.Message}", Theme.Danger);
        }
        finally
        {
            _running = null;
            if (!IsDisposed)
            {
                _verifyButton.Text = "Verify";
                _checksumTextBox.Enabled = true;
            }
        }
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }
}
