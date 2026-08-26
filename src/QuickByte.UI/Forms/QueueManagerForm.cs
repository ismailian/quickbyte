using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using QuickByte.Core.Enums;
using QuickByte.Core.Events;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Forms;

/// <summary>
/// The queue window: every queue on the left, and on the right what is in the
/// selected one, how it transfers, and when it starts itself.
///
/// Edits are applied as they are made rather than gathered behind an OK button.
/// A queue is a live object — one of them may be running while this window is
/// open, and its schedule is being read by another process — so a dialog that
/// held a private copy until it was closed would be describing a queue that no
/// longer exists. The window therefore pushes each change straight through
/// <see cref="IQueueManager"/> and rebuilds itself from the events that come
/// back, exactly as the main window does with downloads.
/// </summary>
public sealed class QueueManagerForm : Form
{
    private const int NameColumnWidth = 250;

    private readonly IQueueManager _queueManager;
    private readonly IDownloadManager _downloadManager;

    private ListBox _queueList;
    private ListView _filesList;
    private FlatTabView _tabs;

    private Label _stateLabel;
    private Label _scheduleNoteLabel;

    private TextBox _nameTextBox;
    private NumericUpDown _concurrentUpDown;
    private NumericUpDown _speedLimitUpDown;

    private CheckBox _scheduleEnabledCheckBox;
    private readonly Dictionary<DayOfWeek, CheckBox> _dayCheckBoxes = new();
    private DateTimePicker _startTimePicker;
    private CheckBox _stopAtCheckBox;
    private DateTimePicker _stopTimePicker;

    private Button _startButton;
    private Button _stopButton;
    private Button _moveUpButton;
    private Button _moveDownButton;
    private Button _removeButton;
    private Button _deleteQueueButton;

    /// <summary>The queue whose settings the right-hand pane is showing, or null.</summary>
    private Guid? _selectedQueueId;

    /// <summary>
    /// Set while the model is being pushed into the controls, so the change
    /// handlers that push the other way stay quiet. Without it, loading a queue
    /// would save it back a dozen times — and a stale numeric box would overwrite
    /// the value that was just loaded.
    /// </summary>
    private bool _loading;

    public QueueManagerForm(IQueueManager queueManager, IDownloadManager downloadManager)
    {
        _queueManager = queueManager;
        _downloadManager = downloadManager;

        BuildUi();
        WireEvents();

        RefreshQueueList();
        SelectFirstQueue();
    }

    // ---------------------------------------------------------------- UI --

    private void BuildUi()
    {
        Text = "Queues & Scheduler";
        Width = 900;
        Height = 640;
        MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Surface;
        Font = Theme.Ui;
        Icon = BrandIcon.App;
        ShowInTaskbar = false;
        MinimizeBox = false;

        Controls.Add(BuildBody());
        Controls.Add(BuildFooter());
        Controls.Add(FormChrome.Header("Queues & Scheduler",
            "Group downloads into queues, cap them, and start them on a schedule.", IconFactory.Queue(40)));
    }

    private Panel BuildBody()
    {
        var body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(16, 12, 16, 8) };

        // Fill first, then the docked-left panel: WinForms lays docked children
        // out last-added-first, so the sidebar has to go in after the pane it
        // sits beside.
        body.Controls.Add(BuildEditorPane());
        body.Controls.Add(BuildQueueSidebar());
        return body;
    }

    private Panel BuildQueueSidebar()
    {
        var panel = new Panel { Dock = DockStyle.Left, Width = 226, BackColor = Theme.Surface, Padding = new Padding(0, 0, 14, 0) };

        _queueList = new ListBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
            IntegralHeight = false,
            ItemHeight = 22
        };

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Theme.Surface,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0)
        };
        var newButton = Theme.StyleButton(new Button { Text = "New queue", Width = 100, Margin = new Padding(0, 0, 6, 0) });
        newButton.Click += (_, _) => OnNewQueueClicked();
        _deleteQueueButton = Theme.StyleButton(new Button { Text = "Delete", Width = 92, Margin = new Padding(0) });
        _deleteQueueButton.Click += (_, _) => OnDeleteQueueClicked();
        buttons.Controls.Add(newButton);
        buttons.Controls.Add(_deleteQueueButton);

        var caption = new Label
        {
            Text = "QUEUES",
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmallBold,
            TextAlign = ContentAlignment.MiddleLeft
        };

        panel.Controls.Add(_queueList);
        panel.Controls.Add(buttons);
        panel.Controls.Add(caption);
        return panel;
    }

    private Panel BuildEditorPane()
    {
        var pane = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };

        _stateLabel = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        };

        _tabs = new FlatTabView { Dock = DockStyle.Fill };
        BuildFilesTab(_tabs.AddPage("Files"));
        BuildOptionsTab(_tabs.AddPage("Options"));
        BuildScheduleTab(_tabs.AddPage("Schedule"));

        pane.Controls.Add(_tabs);
        pane.Controls.Add(_stateLabel);
        return pane;
    }

    private void BuildFilesTab(Panel page)
    {
        page.Padding = new Padding(14, 12, 14, 12);

        _filesList = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            MultiSelect = true,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.Ui
        };
        _filesList.Columns.Add("#", 34);
        _filesList.Columns.Add("File Name", NameColumnWidth);
        _filesList.Columns.Add("Size", 92);
        _filesList.Columns.Add("Status", 110);
        _filesList.SelectedIndexChanged += (_, _) => UpdateCommandStates();

        var side = new Panel { Dock = DockStyle.Right, Width = 124, BackColor = Theme.Surface, Padding = new Padding(12, 0, 0, 0) };
        var sideButtons = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
            BackColor = Theme.Surface
        };

        _moveUpButton = Theme.StyleButton(new Button { Text = "Move up", Width = 112, Margin = new Padding(0, 0, 0, 6) });
        _moveUpButton.Click += (_, _) => MoveSelected(-1);
        _moveDownButton = Theme.StyleButton(new Button { Text = "Move down", Width = 112, Margin = new Padding(0, 0, 0, 6) });
        _moveDownButton.Click += (_, _) => MoveSelected(1);
        _removeButton = Theme.StyleButton(new Button { Text = "Take out", Width = 112, Margin = new Padding(0, 0, 0, 6) });
        _removeButton.Click += (_, _) => RemoveSelected();

        sideButtons.Controls.Add(_moveUpButton);
        sideButtons.Controls.Add(_moveDownButton);
        sideButtons.Controls.Add(_removeButton);

        var hint = new Label
        {
            Text = "Downloads join a queue from the main window: right-click a download and choose \"Add to queue\".",
            Dock = DockStyle.Bottom,
            Height = 58,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall
        };

        side.Controls.Add(sideButtons);
        side.Controls.Add(hint);

        page.Controls.Add(_filesList);
        page.Controls.Add(side);
    }

    private void BuildOptionsTab(Panel page)
    {
        var layout = NewGrid(page, rows: 3);
        int row = 0;

        layout.Controls.Add(Caption("Queue name", "Shown in the sidebar and in the Add to queue menu."), 0, row);
        _nameTextBox = FormChrome.Field();
        _nameTextBox.Margin = new Padding(0, 12, 0, 0);
        _nameTextBox.MaxLength = DownloadQueue.MaxNameLength;
        layout.Controls.Add(_nameTextBox, 1, row);
        layout.SetColumnSpan(_nameTextBox, 2);
        row++;

        _concurrentUpDown = AddNumericRow(layout, row++,
            "Downloads at once",
            "How many of this queue's files transfer together. The app-wide limit still applies.",
            DownloadQueue.MinConcurrentDownloads, DownloadQueue.MaxConcurrentDownloads);

        _speedLimitUpDown = AddNumericRow(layout, row,
            "Queue speed limit (KB/s)",
            "Shared by this queue's downloads; 0 = no limit. Applies at once.",
            0, 1_000_000, increment: 50);
    }

    private void BuildScheduleTab(Panel page)
    {
        page.Padding = new Padding(22, 16, 22, 12);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 6,
            BackColor = Theme.Surface
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 34, 40, 40, 40 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        int row = 0;

        _scheduleEnabledCheckBox = new CheckBox
        {
            Text = "Start this queue automatically",
            AutoSize = true,
            Font = Theme.UiBold,
            ForeColor = Theme.Text,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 6, 0, 0),
            Anchor = AnchorStyles.Left
        };
        layout.Controls.Add(_scheduleEnabledCheckBox, 0, row);
        layout.SetColumnSpan(_scheduleEnabledCheckBox, 2);
        row++;

        layout.Controls.Add(RowLabel("On these days"), 0, row);
        var days = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Theme.Surface,
            WrapContents = false,
            Margin = new Padding(0, 6, 0, 0)
        };
        foreach (DayOfWeek day in new[]
                 {
                     DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday,
                     DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday
                 })
        {
            var box = new CheckBox
            {
                Text = day.ToString()[..3],
                AutoSize = true,
                Font = Theme.UiSmall,
                ForeColor = Theme.Text,
                BackColor = Theme.Surface,
                Margin = new Padding(0, 4, 10, 0)
            };
            box.CheckedChanged += (_, _) => ApplyEdits();
            _dayCheckBoxes[day] = box;
            days.Controls.Add(box);
        }
        layout.Controls.Add(days, 1, row++);

        layout.Controls.Add(RowLabel("Start at"), 0, row);
        _startTimePicker = TimePicker();
        layout.Controls.Add(_startTimePicker, 1, row++);

        _stopAtCheckBox = new CheckBox
        {
            Text = "Stop at",
            AutoSize = true,
            Font = Theme.Ui,
            ForeColor = Theme.Text,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 10, 0, 0),
            Anchor = AnchorStyles.Left
        };
        layout.Controls.Add(_stopAtCheckBox, 0, row);
        _stopTimePicker = TimePicker();
        layout.Controls.Add(_stopTimePicker, 1, row++);

        _scheduleNoteLabel = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            BackColor = Theme.Surface,
            Margin = new Padding(0, 6, 0, 0)
        };
        layout.Controls.Add(_scheduleNoteLabel, 0, row);
        layout.SetColumnSpan(_scheduleNoteLabel, 2);

        page.Controls.Add(layout);
    }

    private static DateTimePicker TimePicker() => new()
    {
        Format = DateTimePickerFormat.Time,
        ShowUpDown = true,
        Width = 108,
        Font = Theme.Ui,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(0, 8, 0, 0)
    };

    private static Label RowLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        ForeColor = Theme.Text,
        Font = Theme.Ui,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 8, 8, 0)
    };

    private Panel BuildFooter()
    {
        var footer = FormChrome.Footer();
        var buttons = FormChrome.ButtonRow();

        // Right-to-left flow: the first one added is the rightmost.
        var closeButton = Theme.StyleButton(new Button { Text = "Close", DialogResult = DialogResult.OK });
        _stopButton = Theme.StyleButton(new Button { Text = "Stop queue", Width = 110, Margin = new Padding(0, 0, 8, 0) });
        _stopButton.Click += (_, _) => OnStopClicked();
        _startButton = Theme.StyleButton(new Button { Text = "Start queue", Width = 110, Margin = new Padding(0, 0, 8, 0) }, primary: true);
        _startButton.Click += (_, _) => OnStartClicked();

        buttons.Controls.Add(closeButton);
        buttons.Controls.Add(_stopButton);
        buttons.Controls.Add(_startButton);
        footer.Controls.Add(buttons);

        AcceptButton = closeButton;
        CancelButton = closeButton;
        return footer;
    }

    // ------------------------------------------------------------ Wiring --

    private void WireEvents()
    {
        _queueList.SelectedIndexChanged += (_, _) => OnQueueSelected();

        // The name is applied when the box is left rather than on every
        // keystroke: each apply persists the queue and rebuilds the list, and
        // doing that per character makes typing feel like it is fighting back.
        _nameTextBox.Leave += (_, _) => ApplyEdits();
        _nameTextBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            ApplyEdits();
        };

        _concurrentUpDown.ValueChanged += (_, _) => ApplyEdits();
        _speedLimitUpDown.ValueChanged += (_, _) => ApplyEdits();
        _scheduleEnabledCheckBox.CheckedChanged += (_, _) => ApplyEdits();
        _stopAtCheckBox.CheckedChanged += (_, _) => ApplyEdits();
        _startTimePicker.ValueChanged += (_, _) => ApplyEdits();
        _stopTimePicker.ValueChanged += (_, _) => ApplyEdits();

        _queueManager.QueuesChanged += OnQueuesChanged;
        _queueManager.QueueStateChanged += OnQueueStateChanged;
        _downloadManager.StatusChanged += OnDownloadStatusChanged;
        _downloadManager.DownloadListChanged += OnDownloadListChanged;

        FormClosed += (_, _) =>
        {
            _queueManager.QueuesChanged -= OnQueuesChanged;
            _queueManager.QueueStateChanged -= OnQueueStateChanged;
            _downloadManager.StatusChanged -= OnDownloadStatusChanged;
            _downloadManager.DownloadListChanged -= OnDownloadListChanged;
        };
    }

    private void OnQueuesChanged(object sender, QueuesChangedEventArgs e)
    {
        if (IsDisposed) return;

        RefreshQueueList();
        RefreshFilesList();
        RefreshState();
    }

    private void OnQueueStateChanged(object sender, QueueStateChangedEventArgs e)
    {
        if (IsDisposed) return;
        RefreshState();
        RefreshFilesList();
    }

    private void OnDownloadStatusChanged(object sender, DownloadStatusChangedEventArgs e)
    {
        if (IsDisposed || _selectedQueueId is null) return;
        if (_queueManager.Find(_selectedQueueId.Value) is not { } queue) return;
        if (!queue.ItemIds.Contains(e.DownloadId)) return;

        RefreshFilesList();
    }

    private void OnDownloadListChanged(object sender, DownloadListChangedEventArgs e)
    {
        if (IsDisposed) return;
        RefreshFilesList();
    }

    // ------------------------------------------------------------ Queues --

    private void RefreshQueueList()
    {
        var queues = _queueManager.Queues;
        Guid? keepSelected = _selectedQueueId;

        _loading = true;
        try
        {
            _queueList.BeginUpdate();
            _queueList.Items.Clear();
            foreach (var queue in queues)
                _queueList.Items.Add(new QueueEntry(queue));
            _queueList.EndUpdate();

            int index = queues.ToList().FindIndex(queue => queue.Id == keepSelected);
            if (index >= 0) _queueList.SelectedIndex = index;
        }
        finally
        {
            _loading = false;
        }

        if (_queueList.SelectedIndex < 0) _selectedQueueId = null;
        UpdateCommandStates();
    }

    private void SelectFirstQueue()
    {
        if (_queueList.Items.Count == 0)
        {
            LoadQueueIntoEditor(null);
            return;
        }
        _queueList.SelectedIndex = 0;
    }

    private void OnQueueSelected()
    {
        if (_loading) return;

        var entry = _queueList.SelectedItem as QueueEntry;
        _selectedQueueId = entry?.Queue.Id;
        LoadQueueIntoEditor(entry is null ? null : _queueManager.Find(entry.Queue.Id));
    }

    private void OnNewQueueClicked()
    {
        var queue = _queueManager.Create(NextQueueName());

        // Straight into renaming it: a queue called "Queue 3" is a placeholder,
        // and the field that fixes that is two tabs away otherwise.
        _selectedQueueId = queue.Id;
        RefreshQueueList();
        LoadQueueIntoEditor(_queueManager.Find(queue.Id));
        _tabs.SelectedIndex = 1;
        _nameTextBox.Focus();
        _nameTextBox.SelectAll();
    }

    private string NextQueueName()
    {
        var existing = _queueManager.Queues.Select(queue => queue.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; ; index++)
        {
            string candidate = $"Queue {index}";
            if (existing.Add(candidate)) return candidate;
        }
    }

    private void OnDeleteQueueClicked()
    {
        if (_selectedQueueId is not { } queueId) return;
        if (_queueManager.Find(queueId) is not { } queue) return;

        string prompt = queue.ItemIds.Count == 0
            ? $"Delete the queue \"{queue.Name}\"?"
            : $"Delete the queue \"{queue.Name}\"?\n\nIts {queue.ItemIds.Count} download(s) stay in the list — they simply "
              + "stop belonging to a queue.";

        if (MessageBox.Show(this, prompt, "Delete Queue", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        _queueManager.Delete(queueId);
        _selectedQueueId = null;
        RefreshQueueList();
        SelectFirstQueue();
    }

    // ------------------------------------------------------------ Editor --

    private void LoadQueueIntoEditor(DownloadQueue queue)
    {
        _loading = true;
        try
        {
            bool has = queue is not null;

            _nameTextBox.Text = has ? queue.Name : string.Empty;
            _concurrentUpDown.Value = has
                ? Math.Clamp(queue.ConcurrentDownloads, DownloadQueue.MinConcurrentDownloads, DownloadQueue.MaxConcurrentDownloads)
                : DownloadQueue.MinConcurrentDownloads;
            _speedLimitUpDown.Value = has
                ? Math.Clamp(queue.SpeedLimitBytesPerSecond / ByteFormatter.BytesPerKilobyte, 0, 1_000_000)
                : 0;

            var schedule = has ? queue.Schedule : new QueueSchedule();
            _scheduleEnabledCheckBox.Checked = schedule.Enabled;
            foreach (var pair in _dayCheckBoxes)
                pair.Value.Checked = schedule.RunsOn(pair.Key);
            _startTimePicker.Value = TimeToPickerValue(schedule.StartTime);
            _stopAtCheckBox.Checked = schedule.StopAtEnabled;
            _stopTimePicker.Value = TimeToPickerValue(schedule.StopTime);
        }
        finally
        {
            _loading = false;
        }

        RefreshFilesList();
        RefreshState();
        UpdateCommandStates();
    }

    /// <summary>
    /// A <see cref="DateTimePicker"/> in time mode still carries a date, and it
    /// has to be one inside the control's own range — today's, with the schedule's
    /// time of day on it.
    /// </summary>
    private static DateTime TimeToPickerValue(TimeSpan time)
    {
        var clamped = time < TimeSpan.Zero || time >= TimeSpan.FromDays(1) ? TimeSpan.Zero : time;
        return DateTime.Today + clamped;
    }

    /// <summary>
    /// Pushes every control's value back onto the queue in one go. One call per
    /// change rather than a partial update per control: <see cref="IQueueManager.Update"/>
    /// takes a whole queue, and reading them all is cheaper than reasoning about
    /// which one moved.
    /// </summary>
    private void ApplyEdits()
    {
        if (_loading || _selectedQueueId is not { } queueId) return;
        if (_queueManager.Find(queueId) is not { } queue) return;

        queue.Name = _nameTextBox.Text;
        queue.ConcurrentDownloads = (int)_concurrentUpDown.Value;
        queue.SpeedLimitBytesPerSecond = (long)_speedLimitUpDown.Value * ByteFormatter.BytesPerKilobyte;
        queue.Schedule = new QueueSchedule
        {
            Enabled = _scheduleEnabledCheckBox.Checked,
            Days = SelectedDays(),
            StartTime = _startTimePicker.Value.TimeOfDay,
            StopAtEnabled = _stopAtCheckBox.Checked,
            StopTime = _stopTimePicker.Value.TimeOfDay
        };

        _queueManager.Update(queue);
        RefreshState();
    }

    private ScheduleDays SelectedDays()
    {
        var days = ScheduleDays.None;
        foreach (var pair in _dayCheckBoxes)
        {
            if (pair.Value.Checked) days |= QueueSchedule.ToFlag(pair.Key);
        }
        return days;
    }

    // ------------------------------------------------------------- Files --

    private void RefreshFilesList()
    {
        var queue = _selectedQueueId is { } id ? _queueManager.Find(id) : null;
        var downloads = _downloadManager.Downloads.ToDictionary(item => item.Id);

        // The selection is restored by download id rather than by row index: the
        // list is rebuilt on every status change, and a row number means
        // something different after a Move.
        var selectedIds = _filesList.SelectedItems.Cast<ListViewItem>()
            .Select(row => (Guid)row.Tag).ToHashSet();

        _filesList.BeginUpdate();
        _filesList.Items.Clear();

        if (queue is not null)
        {
            int position = 1;
            foreach (var downloadId in queue.ItemIds)
            {
                if (!downloads.TryGetValue(downloadId, out var item)) continue;

                var row = new ListViewItem(position.ToString()) { Tag = downloadId };
                row.SubItems.Add(item.FileName);
                row.SubItems.Add(ByteFormatter.FormatBytes(item.TotalBytes));
                row.SubItems.Add(StatusText(item));
                row.Selected = selectedIds.Contains(downloadId);
                _filesList.Items.Add(row);
                position++;
            }
        }

        _filesList.EndUpdate();
        UpdateCommandStates();
    }

    private static string StatusText(DownloadItem item) => item.Status switch
    {
        DownloadStatus.Queued => "Waiting",
        DownloadStatus.Completed => "Complete",
        _ => item.Status.ToString()
    };

    private void MoveSelected(int offset)
    {
        if (_selectedQueueId is not { } queueId) return;
        if (_filesList.SelectedItems.Count != 1) return;

        var downloadId = (Guid)_filesList.SelectedItems[0].Tag;
        if (!_queueManager.Move(queueId, downloadId, offset)) return;

        RefreshFilesList();
        SelectFile(downloadId);
    }

    private void SelectFile(Guid downloadId)
    {
        foreach (ListViewItem row in _filesList.Items)
        {
            if ((Guid)row.Tag != downloadId) continue;
            row.Selected = true;
            row.Focused = true;
            row.EnsureVisible();
            return;
        }
    }

    private void RemoveSelected()
    {
        var ids = _filesList.SelectedItems.Cast<ListViewItem>().Select(row => (Guid)row.Tag).ToList();
        if (ids.Count == 0) return;

        _queueManager.RemoveFromQueues(ids);
        RefreshFilesList();
    }

    // ------------------------------------------------------------ State --

    private void RefreshState()
    {
        if (_selectedQueueId is not { } queueId || _queueManager.Find(queueId) is not { } queue)
        {
            _stateLabel.Text = "No queue selected. Create one to group downloads and give them their own schedule.";
            _stateLabel.ForeColor = Theme.TextMuted;
            _scheduleNoteLabel.Text = string.Empty;
            UpdateCommandStates();
            return;
        }

        bool running = _queueManager.StateOf(queueId) == QueueState.Running;
        DateTime? nextRun = _queueManager.NextRunAt(queueId);

        if (running)
        {
            _stateLabel.Text = $"\"{queue.Name}\" is running — {queue.ClampConcurrency()} download(s) at a time.";
            _stateLabel.ForeColor = Theme.Accent;
        }
        else if (nextRun is { } when)
        {
            _stateLabel.Text = $"\"{queue.Name}\" is idle. Next scheduled start: {when:ddd d MMM, HH:mm}.";
            _stateLabel.ForeColor = Theme.TextMuted;
        }
        else
        {
            _stateLabel.Text = $"\"{queue.Name}\" is idle, with no schedule. Start it here whenever you like.";
            _stateLabel.ForeColor = Theme.TextMuted;
        }

        _scheduleNoteLabel.Text = ScheduleNote(queue, nextRun);
        UpdateCommandStates();
    }

    /// <summary>
    /// The paragraph under the schedule fields. It answers the question a
    /// schedule immediately raises — "does this work when QuickByte is closed?" —
    /// because the honest answer depends on whether the scheduler agent is there,
    /// and a user who has to find that out at 03:00 has been failed by the window.
    /// </summary>
    private static string ScheduleNote(DownloadQueue queue, DateTime? nextRun)
    {
        if (!queue.Schedule.Enabled)
            return "Not scheduled. The queue only runs when you start it here.";

        if (queue.Schedule.Days == ScheduleDays.None)
            return "No days are selected, so this schedule will never fire. Tick at least one day.";

        string next = nextRun is { } when ? $"Next start: {when:ddd d MMM, HH:mm}. " : string.Empty;

        if (!QueueAgentRegistration.IsAvailable)
            return next + "QuickByte must be running (a window or the notification area) at that time — "
                        + "the background scheduler is not installed with this copy.";

        return next + "QuickByte does not need to be open: the background scheduler starts with Windows "
                    + "and launches QuickByte when the time comes.";
    }

    private void UpdateCommandStates()
    {
        bool hasQueue = _selectedQueueId is not null;
        bool running = hasQueue && _queueManager.StateOf(_selectedQueueId.Value) == QueueState.Running;
        int selectedFiles = _filesList?.SelectedItems.Count ?? 0;

        _startButton.Enabled = hasQueue && !running;
        _stopButton.Enabled = running;
        _deleteQueueButton.Enabled = hasQueue;

        _nameTextBox.Enabled = hasQueue;
        _concurrentUpDown.Enabled = hasQueue;
        _speedLimitUpDown.Enabled = hasQueue;
        _scheduleEnabledCheckBox.Enabled = hasQueue;
        _stopAtCheckBox.Enabled = hasQueue;
        _startTimePicker.Enabled = hasQueue && _scheduleEnabledCheckBox.Checked;
        _stopTimePicker.Enabled = hasQueue && _scheduleEnabledCheckBox.Checked && _stopAtCheckBox.Checked;
        foreach (var box in _dayCheckBoxes.Values)
            box.Enabled = hasQueue && _scheduleEnabledCheckBox.Checked;

        _moveUpButton.Enabled = selectedFiles == 1;
        _moveDownButton.Enabled = selectedFiles == 1;
        _removeButton.Enabled = selectedFiles > 0;
    }

    private void OnStartClicked()
    {
        if (_selectedQueueId is not { } queueId) return;
        _queueManager.Start(queueId);
        RefreshState();
    }

    private void OnStopClicked()
    {
        if (_selectedQueueId is not { } queueId) return;
        _queueManager.Stop(queueId);
        RefreshState();
    }

    // ------------------------------------------------------------- Grid --

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
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));

        for (int i = 0; i < rows; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        page.Controls.Add(layout);
        return layout;
    }

    private static NumericUpDown AddNumericRow(
        TableLayoutPanel layout, int row, string caption, string hint, int min, int max, int increment = 1)
    {
        var captionBlock = Caption(caption, hint);
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

    private static Panel Caption(string caption, string hint)
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

    /// <summary>One row of the queue sidebar. The label is what the ListBox draws.</summary>
    private sealed class QueueEntry
    {
        public QueueEntry(DownloadQueue queue) => Queue = queue;

        public DownloadQueue Queue { get; }

        public override string ToString()
        {
            int count = Queue.ItemIds.Count;
            string scheduled = Queue.Schedule.Enabled ? " ⏱" : string.Empty;
            return $"{Queue.Name}  ({count}){scheduled}";
        }
    }
}
