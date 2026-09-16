using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Forms;

/// <summary>
/// Adds many downloads at once: a pasted list of links, a web page's source, a
/// text file of links, or a numbered pattern such as
/// <c>https://example.com/photo[001-250].jpg</c> (see <see cref="BatchLinks"/>).
///
/// Every link is probed before anything is added, a few at a time, for the same
/// reason Add Download probes one: the file name a download is saved under is
/// settled when it is added, and a link's own last path segment is frequently not
/// it. The list then shows what each link turned out to be, and a link that
/// failed — or that is not wanted — is simply left unticked.
///
/// Like Add Download this window only resolves links; <see cref="MainForm"/> adds
/// them when it closes. There is no details window per download — two hundred of
/// them opening at once is not a batch, it is a denial of service on the desktop.
/// </summary>
public sealed class BatchDownloadForm : Form
{
    /// <summary>How many links are probed at once: quick for a long list, polite to one server.</summary>
    private const int ParallelProbes = 6;

    private readonly ISettingsService _settingsService;
    private readonly IRemoteFileInfoProvider _fileInfoProvider;
    private readonly IReadOnlyList<DownloadQueue> _queues;
    private readonly CancellationTokenSource _closing = new();

    private TextBox _linksTextBox = null!;
    private Button _checkButton = null!;
    private Label _statusLabel = null!;
    private ListView _resultsList = null!;
    private TextBox _saveFolderTextBox = null!;
    private ConnectionCountBox _connectionsBox = null!;
    private ComboBox _queueComboBox = null!;
    private Button _addButton = null!;

    private bool _checking;

    /// <summary>The downloads to add, set only when the user confirms; its presence is the confirmation.</summary>
    public IReadOnlyList<DownloadRequest>? Results { get; private set; }

    /// <summary>The existing queue chosen, or null for "start now" or a new queue.</summary>
    public Guid? SelectedQueueId { get; private set; }

    /// <summary>The name for a queue to create for this batch, or null.</summary>
    public string? NewQueueName { get; private set; }

    public BatchDownloadForm(ISettingsService settingsService, IRemoteFileInfoProvider fileInfoProvider, IReadOnlyList<DownloadQueue> queues)
    {
        _settingsService = settingsService;
        _fileInfoProvider = fileInfoProvider;
        _queues = queues;

        BuildUi();
        FormClosed += (_, _) =>
        {
            _closing.Cancel();
            _closing.Dispose();
        };
    }

    private void BuildUi()
    {
        Text = "Batch Download";
        Width = 760;
        Height = 640;
        MinimumSize = new Size(640, 560);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        BackColor = Theme.Surface;
        Font = Theme.Ui;
        Icon = BrandIcon.App;

        Controls.Add(BuildBody());
        Controls.Add(BuildFooter());
        Controls.Add(FormChrome.Header("Batch Download",
            "Paste links or a page's source, import a file, or use a pattern like photo[001-250].jpg.",
            IconFactory.BatchDownload(40)));
    }

    private Panel BuildBody()
    {
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(24, 12, 24, 8) };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 7, BackColor = Theme.Surface };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));

        // Links · (buttons) · status · results (fills) · save to · connections · queue.
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

        int row = 0;

        layout.Controls.Add(FormChrome.FieldLabel("Links"), 0, row);
        _linksTextBox = FormChrome.Field();
        _linksTextBox.Multiline = true;
        _linksTextBox.ScrollBars = ScrollBars.Vertical;
        _linksTextBox.AcceptsReturn = true;
        _linksTextBox.WordWrap = false;
        _linksTextBox.MaxLength = (int)BatchLinks.MaxImportBytes;
        layout.Controls.Add(_linksTextBox, 1, row);
        layout.SetRowSpan(_linksTextBox, 2);

        _checkButton = Theme.StyleButton(new Button { Text = "Check Links", Dock = DockStyle.Fill, Margin = new Padding(8, 6, 0, 4) }, primary: true);
        _checkButton.Click += async (_, _) => await CheckAsync();
        layout.Controls.Add(_checkButton, 2, row++);

        var importButton = Theme.StyleButton(new Button { Text = "Import File…", Dock = DockStyle.Fill, Margin = new Padding(8, 4, 0, 6) });
        importButton.Click += (_, _) => OnImportClicked();
        layout.Controls.Add(importButton, 2, row++);

        _statusLabel = new Label
        {
            Text = "One link per line, or anything with links in it.",
            Dock = DockStyle.Fill,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };
        layout.Controls.Add(_statusLabel, 1, row);
        layout.SetColumnSpan(_statusLabel, 2);
        row++;

        _resultsList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
            Margin = new Padding(0, 2, 0, 8)
        };
        _resultsList.Columns.Add("File name", 220);
        _resultsList.Columns.Add("Size", 90);
        _resultsList.Columns.Add("Status", 150);
        _resultsList.Columns.Add("Address", 400);
        _resultsList.ItemCheck += (_, e) =>
        {
            // A link that could not be resolved has nothing to add.
            if (_resultsList.Items[e.Index].Tag is not BatchEntry { Info: not null }) e.NewValue = CheckState.Unchecked;
        };
        _resultsList.ItemChecked += (_, _) => UpdateAddButton();
        layout.Controls.Add(_resultsList, 0, row);
        layout.SetColumnSpan(_resultsList, 3);
        row++;

        layout.Controls.Add(FormChrome.FieldLabel("Save to"), 0, row);
        _saveFolderTextBox = FormChrome.Field();
        _saveFolderTextBox.Text = UserPath.Shorten(_settingsService.Current.DefaultDownloadFolder);
        layout.Controls.Add(_saveFolderTextBox, 1, row);
        var browseButton = Theme.StyleButton(new Button { Text = "Browse…", Dock = DockStyle.Fill, Margin = new Padding(8, 4, 0, 4) });
        browseButton.Click += (_, _) => OnBrowseClicked();
        layout.Controls.Add(browseButton, 2, row++);

        layout.Controls.Add(FormChrome.FieldLabel("Connections"), 0, row);
        _connectionsBox = new ConnectionCountBox
        {
            Width = 72,
            Anchor = AnchorStyles.Left,
            Connections = _settingsService.Current.DefaultConnectionsCount
        };
        layout.Controls.Add(_connectionsBox, 1, row++);

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

        // A new queue is the default here, unlike on Add Download: a batch is
        // usually something to leave running, and a queue is what lets it run a
        // few at a time, on a schedule, and shut the machine down afterwards.
        _queueComboBox.Items.Add(new QueueChoice(QueueChoiceKind.NewQueue, null, $"Add to a new queue, \"{DefaultQueueName()}\""));
        _queueComboBox.Items.Add(new QueueChoice(QueueChoiceKind.StartNow, null, "Start them all now"));
        foreach (var queue in _queues)
            _queueComboBox.Items.Add(new QueueChoice(QueueChoiceKind.Existing, queue.Id, $"Add to \"{queue.Name}\""));
        _queueComboBox.SelectedIndex = 0;
        layout.Controls.Add(_queueComboBox, 1, row);
        layout.SetColumnSpan(_queueComboBox, 2);

        body.Controls.Add(layout);
        return body;
    }

    private Panel BuildFooter()
    {
        var footer = FormChrome.Footer();
        var buttons = FormChrome.ButtonRow();

        _addButton = Theme.StyleButton(new Button { Text = "Add Downloads", Width = 140, Enabled = false }, primary: true);
        _addButton.Click += (_, _) => OnAddClicked();

        var cancelButton = Theme.StyleButton(new Button { Text = "Cancel" });
        cancelButton.Click += (_, _) => Close();

        buttons.Controls.Add(_addButton);
        buttons.Controls.Add(cancelButton);
        footer.Controls.Add(buttons);

        CancelButton = cancelButton;
        return footer;
    }

    private static string DefaultQueueName() => $"Batch {DateTime.Now:d MMM HH:mm}";

    private void OnImportClicked()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Import links",
            Filter = "Text and web pages (*.txt;*.htm;*.html)|*.txt;*.htm;*.html|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _linksTextBox.Text = BatchLinks.ReadImportFile(dialog.FileName);
            _ = CheckAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not import that file: {ex.Message}", Theme.Danger);
        }
    }

    private void OnBrowseClicked()
    {
        using var dialog = new FolderBrowserDialog { SelectedPath = UserPath.Expand(_saveFolderTextBox.Text) };
        if (dialog.ShowDialog(this) == DialogResult.OK)
            _saveFolderTextBox.Text = UserPath.Shorten(dialog.SelectedPath);
    }

    /// <summary>
    /// Extracts the links and probes every one, <see cref="ParallelProbes"/> at a
    /// time, filling the list in as the answers come back.
    /// </summary>
    private async Task CheckAsync()
    {
        if (_checking) return;

        IReadOnlyList<string> links;
        try
        {
            links = BatchLinks.ExtractLinks(_linksTextBox.Text);
        }
        catch (ArgumentException ex)
        {
            SetStatus(ex.Message, Theme.Danger);
            return;
        }

        if (links.Count == 0)
        {
            SetStatus("No http, https, ftp or ftps links found.", Theme.Danger);
            return;
        }

        _checking = true;
        _checkButton.Enabled = false;
        _addButton.Enabled = false;

        _resultsList.BeginUpdate();
        _resultsList.Items.Clear();
        var rows = links.Select(link =>
        {
            // The address the user pasted, with any login taken out of it — the
            // column is on screen, and the request carries the login instead.
            var split = UrlCredentials.Extract(link);
            var row = new ListViewItem(new[] { FileNameHelper.FileNameFromUrl(split.Url), "", "Waiting…", split.Url })
            {
                ForeColor = Theme.TextMuted,
                Tag = new BatchEntry(split.Url, split.Credentials)
            };
            _resultsList.Items.Add(row);
            return (Row: row, Entry: (BatchEntry)row.Tag);
        }).ToList();
        _resultsList.EndUpdate();

        int done = 0;
        SetStatus($"Checking {links.Count} link{(links.Count == 1 ? "" : "s")}…", Theme.TextMuted);

        using var gate = new SemaphoreSlim(ParallelProbes);
        var token = _closing.Token;

        var probes = rows.Select(async entry =>
        {
            await gate.WaitAsync(token);
            try
            {
                if (IsDisposed) return;
                entry.Row.SubItems[2].Text = "Checking…";
                var info = await _fileInfoProvider.GetFileInfoAsync(
                    entry.Entry.Url, new RequestOptions { Credentials = entry.Entry.Credentials }, token);
                if (IsDisposed) return;

                entry.Entry.Info = info;
                entry.Row.SubItems[0].Text = info.FileName;
                entry.Row.SubItems[1].Text = info.HasKnownSize ? ByteFormatter.FormatBytes(info.ContentLength) : "Unknown";
                entry.Row.SubItems[2].Text = info.SupportsRangeRequests ? "Ready" : "Ready (one connection)";
                entry.Row.ForeColor = Theme.Text;
                entry.Row.Checked = true;
            }
            catch (OperationCanceledException)
            {
                // The window closed.
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                entry.Row.SubItems[2].Text = ex is AuthenticationRequiredException ? "Needs a login" : ex.Message;
                entry.Row.ForeColor = Theme.Danger;
            }
            finally
            {
                gate.Release();
                if (!IsDisposed)
                {
                    done++;
                    SetStatus($"Checked {done} of {rows.Count}…", Theme.TextMuted);
                }
            }
        }).ToList();

        try
        {
            await Task.WhenAll(probes);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (IsDisposed) return;

        _checking = false;
        _checkButton.Enabled = true;

        int ready = rows.Count(r => r.Entry.Info is not null);
        int failed = rows.Count - ready;
        SetStatus(failed == 0
                ? $"All {ready} link{(ready == 1 ? " is" : "s are")} ready."
                : $"{ready} ready, {failed} could not be checked and will be left out.",
            failed == 0 ? Theme.Success : Theme.Warning);
        UpdateAddButton();
    }

    private void UpdateAddButton()
    {
        int count = _resultsList.CheckedItems.Count;
        _addButton.Enabled = !_checking && count > 0;
        _addButton.Text = count == 1 ? "Add 1 Download" : $"Add {count} Downloads";
    }

    private void OnAddClicked()
    {
        if (string.IsNullOrWhiteSpace(_saveFolderTextBox.Text))
        {
            SetStatus("Choose a folder to save to.", Theme.Danger);
            return;
        }

        string folder = UserPath.Expand(_saveFolderTextBox.Text);
        var choice = (QueueChoice)_queueComboBox.SelectedItem!;
        bool startNow = choice.Kind == QueueChoiceKind.StartNow;

        Results = _resultsList.CheckedItems
            .Cast<ListViewItem>()
            .Select(row => row.Tag)
            .OfType<BatchEntry>()
            .Where(entry => entry.Info is not null)
            .Select(entry => new DownloadRequest(
                entry.Url, entry.Info!, folder, entry.Info!.FileName,
                entry.Info.SupportsRangeRequests ? _connectionsBox.Connections : 1)
            {
                Credentials = entry.Credentials,
                StartImmediately = startNow
            })
            .ToList();

        SelectedQueueId = choice.Kind == QueueChoiceKind.Existing ? choice.QueueId : null;
        NewQueueName = choice.Kind == QueueChoiceKind.NewQueue ? DefaultQueueName() : null;
        Close();
    }

    private void SetStatus(string text, Color color)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
    }

    /// <summary>One pasted link: where it points, the login lifted out of it, and what the probe found.</summary>
    private sealed class BatchEntry
    {
        public BatchEntry(string url, DownloadCredentials? credentials)
        {
            Url = url;
            Credentials = credentials;
        }

        public string Url { get; }
        public DownloadCredentials? Credentials { get; }
        public RemoteFileInfo? Info { get; set; }
    }

    private enum QueueChoiceKind { NewQueue, StartNow, Existing }

    private sealed class QueueChoice
    {
        public QueueChoice(QueueChoiceKind kind, Guid? queueId, string label)
        {
            Kind = kind;
            QueueId = queueId;
            Label = label;
        }

        public QueueChoiceKind Kind { get; }
        public Guid? QueueId { get; }
        private string Label { get; }

        public override string ToString() => Label;
    }
}
