using System.Linq;
using System.Drawing;
using System.Windows.Forms;
using QuickByte.Core.Enums;
using QuickByte.Core.Events;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Forms;

/// <summary>
/// Shows full detail for a single download, styled after IDM's download
/// window: a "Download status" tab with URL/size/speed/ETA fields, an overall
/// progress bar, the connection start-position/progress segments bar, and a
/// connections grid — plus "Speed Limiter" and "Options on completion" tabs.
///
/// Multiple instances can be open at once (one per download). Every instance
/// subscribes to <see cref="IDownloadManager"/>, not to the service directly:
/// the manager re-publishes the same events already marshaled onto the UI
/// thread, so these handlers can touch controls safely and every open window
/// shows identical numbers at the same time.
/// </summary>
public sealed class DownloadDetailsForm : Form
{
    private const int ConnectionColumnId = 0;
    private const int ConnectionColumnDownloaded = 1;
    private const int ConnectionColumnProgress = 2;
    private const int ConnectionColumnInfo = 3;

    private readonly DownloadItem _item;
    private readonly IDownloadManager _downloadManager;
    private readonly ToolTip _toolTip = new();

    private readonly Dictionary<int, ListViewItem> _connectionRowsById = new();
    private readonly ProgressAnimator<int> _connectionProgress = new();
    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = ProgressAnimation.FrameIntervalMilliseconds };

    private BufferedLabel _fileNameLabel = null!;
    private BufferedLabel _urlLabel = null!;
    private BufferedLabel _statusValueLabel = null!;
    private BufferedLabel _sizeValueLabel = null!;
    private BufferedLabel _downloadedValueLabel = null!;
    private BufferedLabel _speedValueLabel = null!;
    private BufferedLabel _etaValueLabel = null!;
    private BufferedLabel _resumeValueLabel = null!;
    private BufferedLabel _connectionsValueLabel = null!;
    private SmoothProgressBar _overallProgressBar = null!;
    private ConnectionSegmentsBar _segmentsBar = null!;
    private BufferedListView _connectionsListView = null!;

    private Panel _detailsPanel = null!;
    private Button _hideDetailsButton = null!;
    private Button _pauseResumeButton = null!;
    private Button _cancelButton = null!;
    private Button _retryButton = null!;
    private Button _openFolderButton = null!;
    private bool _detailsExpanded = false;
    private bool _openOnCompleteRequested;

    public DownloadDetailsForm(DownloadItem item, IDownloadManager downloadManager)
    {
        _item = item;
        _downloadManager = downloadManager;

        BuildUi();
        WireEvents();
        RefreshStaticFields();
        RefreshDynamicFields(immediate: true);

        _animationTimer.Tick += (_, _) => OnAnimationTick();
        FormClosed += (_, _) =>
        {
            _animationTimer.Dispose();
            _toolTip.Dispose();
        };
    }

    // ---------------------------------------------------------------- UI --

    private void BuildUi()
    {
        Text = _item.FileName;
        Width = 600;
        Height = 420;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(600, 420);
        MaximumSize = new Size(600, 800);
        MaximizeBox = false;
        BackColor = Theme.Surface;
        Font = Theme.Ui;
        Icon = CreateWindowIcon();

        var tabs = new FlatTabView { Dock = DockStyle.Fill };
        BuildStatusTab(tabs.AddPage("Download status"));
        BuildSpeedLimiterTab(tabs.AddPage("Speed Limiter"));
        BuildOptionsTab(tabs.AddPage("Options on completion"));

        Controls.Add(tabs);
        Controls.Add(BuildButtonsPanel());
        Controls.Add(BuildHeader());
    }

    private Icon CreateWindowIcon()
    {
        using var bmp = IconFactory.CategoryIcon(FileCategoryHelper.GetCategory(_item.FileName), 32);
        return Icon.FromHandle(bmp.GetHicon());
    }

    private Panel BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 62, BackColor = Theme.Surface, Padding = new Padding(16, 10, 16, 8) };
        header.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        var icon = new PictureBox
        {
            Image = IconFactory.CategoryIcon(FileCategoryHelper.GetCategory(_item.FileName), 32),
            SizeMode = PictureBoxSizeMode.CenterImage,
            Dock = DockStyle.Left,
            Width = 34,
            BackColor = Theme.Surface
        };

        var textPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 0, 0), BackColor = Theme.Surface };
        _fileNameLabel = new BufferedLabel
        {
            Text = _item.FileName,
            Dock = DockStyle.Top,
            Height = 22,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            ForeColor = Theme.Text,
            AutoEllipsis = true
        };
        _urlLabel = new BufferedLabel
        {
            Text = _item.Url,
            Dock = DockStyle.Top,
            Height = 18,
            Font = Theme.UiSmall,
            ForeColor = Theme.Accent,
            AutoEllipsis = true,
            Cursor = Cursors.Hand
        };
        _urlLabel.Click += (_, _) => CopyUrlToClipboard();
        _toolTip.SetToolTip(_urlLabel, "Click to copy the download URL");
        _toolTip.SetToolTip(_fileNameLabel, _item.FullPath);

        textPanel.Controls.Add(_urlLabel);
        textPanel.Controls.Add(_fileNameLabel);

        header.Controls.Add(textPanel);
        header.Controls.Add(icon);
        return header;
    }

    private void CopyUrlToClipboard()
    {
        try { Clipboard.SetText(_item.Url); } catch { /* clipboard can be locked by another app */ }
    }

    private void BuildStatusTab(Panel page)
    {
        page.Padding = new Padding(16, 14, 16, 12);

        // --- Info grid -----------------------------------------------------
        var infoPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 4,
            RowCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Surface
        };
        for (int i = 0; i < 4; i++)
            infoPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        infoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118));
        infoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        infoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
        infoPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _statusValueLabel = AddInfoCell(infoPanel, "Status:", 0, 0, Theme.Accent, bold: true);
        _sizeValueLabel = AddInfoCell(infoPanel, "File size:", 2, 0);
        _downloadedValueLabel = AddInfoCell(infoPanel, "Downloaded:", 0, 1);
        _speedValueLabel = AddInfoCell(infoPanel, "Transfer rate:", 2, 1);
        _etaValueLabel = AddInfoCell(infoPanel, "Time left:", 0, 2);
        _connectionsValueLabel = AddInfoCell(infoPanel, "Connections:", 2, 2);
        _resumeValueLabel = AddInfoCell(infoPanel, "Resumable:", 0, 3);

        // --- Overall progress ----------------------------------------------
        var progressPanel = new Panel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(0, 12, 0, 6), BackColor = Theme.Surface };
        _overallProgressBar = new SmoothProgressBar { Dock = DockStyle.Fill, BarColor = Theme.Accent };
        progressPanel.Controls.Add(_overallProgressBar);

        // --- Details toggle --------------------------------------------------
        var toggleRow = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Theme.Surface };
        _hideDetailsButton = new Button
        {
            Text = "▼  Show details",
            AutoSize = false,
            Width = 130,
            Height = 26,
            Dock = DockStyle.Left,
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.Surface,
            ForeColor = Theme.Accent,
            Font = Theme.UiSmall,
            Cursor = Cursors.Hand,
            TextAlign = ContentAlignment.MiddleLeft
        };
        _hideDetailsButton.FlatAppearance.BorderSize = 0;
        _hideDetailsButton.FlatAppearance.MouseOverBackColor = Theme.AccentSoft;
        _hideDetailsButton.Click += (_, _) => ToggleDetailsSection();
        toggleRow.Controls.Add(_hideDetailsButton);

        // --- Connections ------------------------------------------------------
        _detailsPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface };

        var caption = new Label
        {
            Text = "Start positions and download progress by connections",
            Dock = DockStyle.Top,
            Height = 20,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall
        };
        _segmentsBar = new ConnectionSegmentsBar { Dock = DockStyle.Top, Height = 24 };
        var segmentsSpacer = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Theme.Surface };

        _connectionsListView = BuildConnectionsListView();
        var listFrame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Theme.Border };
        listFrame.Controls.Add(_connectionsListView);

        _detailsPanel.Controls.Add(listFrame);
        _detailsPanel.Controls.Add(segmentsSpacer);
        _detailsPanel.Controls.Add(_segmentsBar);
        _detailsPanel.Controls.Add(caption);

        page.Controls.Add(_detailsPanel);
        page.Controls.Add(toggleRow);
        page.Controls.Add(progressPanel);
        page.Controls.Add(infoPanel);
    }

    private static BufferedLabel AddInfoCell(TableLayoutPanel panel, string caption, int column, int row, Color? valueColor = null, bool bold = false)
    {
        var captionLabel = new Label
        {
            Text = caption,
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 5, 8, 5)
        };
        var valueLabel = new BufferedLabel
        {
            Text = "—",
            AutoSize = true,
            ForeColor = valueColor ?? Theme.Text,
            Font = bold ? Theme.UiBold : Theme.Ui,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 4, 12, 4)
        };
        panel.Controls.Add(captionLabel, column, row);
        panel.Controls.Add(valueLabel, column + 1, row);
        return valueLabel;
    }

    /// <summary>
    /// The per-download speed cap. Edits are pushed straight through
    /// <see cref="IDownloadManager.SetSpeedLimit"/> with no Apply button,
    /// because the limiter behind it is a live object: the new rate reaches a
    /// transfer that is already running, so making the user confirm would only
    /// hide that.
    /// </summary>
    private void BuildSpeedLimiterTab(Panel page)
    {
        page.Padding = new Padding(20, 18, 20, 16);

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Surface
        };

        bool limited = _item.SpeedLimitBytesPerSecond > 0;
        var enableCheckBox = StyleCheckBox(new CheckBox
        {
            Text = "Limit the maximum download speed for this download",
            Checked = limited
        });

        var speedRow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(22, 10, 0, 0) };
        var speedLabel = new Label { Text = "Maximum speed:", AutoSize = true, ForeColor = Theme.Text, Margin = new Padding(0, 6, 8, 0) };
        var speedUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 1_000_000,
            Increment = 50,
            Value = limited
                ? Math.Clamp(_item.SpeedLimitBytesPerSecond / ByteFormatter.BytesPerKilobyte, 1, 1_000_000)
                : 500,
            Width = 96,
            Enabled = limited,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Ui,
            TextAlign = HorizontalAlignment.Right
        };
        var unitLabel = new Label { Text = "KB/s", AutoSize = true, ForeColor = Theme.TextMuted, Margin = new Padding(8, 6, 0, 0) };
        speedRow.Controls.Add(speedLabel);
        speedRow.Controls.Add(speedUpDown);
        speedRow.Controls.Add(unitLabel);

        var summary = new BufferedLabel
        {
            AutoSize = true,
            Font = Theme.UiSmall,
            Margin = new Padding(0, 20, 0, 0)
        };

        var note = new Label
        {
            Text = "Applies immediately, including to a transfer already in progress. " +
                   "The limit is shared by all of this download's connections.",
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            Margin = new Padding(0, 8, 0, 0)
        };

        void RefreshSummary(long bytesPerSecond)
        {
            summary.Text = DescribeSpeedLimits(bytesPerSecond);
            summary.ForeColor = bytesPerSecond > 0 ? Theme.Accent : Theme.TextMuted;
        }

        void ApplyFromControls()
        {
            speedUpDown.Enabled = enableCheckBox.Checked;
            long bytesPerSecond = enableCheckBox.Checked
                ? (long)speedUpDown.Value * ByteFormatter.BytesPerKilobyte
                : 0;

            _downloadManager.SetSpeedLimit(_item.Id, bytesPerSecond);
            RefreshSummary(bytesPerSecond);
        }

        enableCheckBox.CheckedChanged += (_, _) => ApplyFromControls();
        speedUpDown.ValueChanged += (_, _) => ApplyFromControls();

        // Seeded rather than applied: opening the window must not rewrite (and
        // re-persist) a limit the user has not touched.
        RefreshSummary(_item.SpeedLimitBytesPerSecond);

        stack.Controls.Add(enableCheckBox);
        stack.Controls.Add(speedRow);
        stack.Controls.Add(summary);
        stack.Controls.Add(note);
        page.Controls.Add(stack);
    }

    private string DescribeSpeedLimits(long bytesPerSecond)
    {
        string own = bytesPerSecond > 0
            ? $"Capped at {ByteFormatter.FormatSpeed(bytesPerSecond)}."
            : "Running at full speed.";

        long global = _downloadManager.GlobalSpeedLimitBytesPerSecond;
        return global > 0
            ? $"{own}  A global limit of {ByteFormatter.FormatSpeed(global)} is also shared across all downloads."
            : own;
    }

    private void BuildOptionsTab(Panel page)
    {
        page.Padding = new Padding(20, 18, 20, 16);

        var stack = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Theme.Surface
        };

        var openFolderCheckBox = StyleCheckBox(new CheckBox { Text = "Open the containing folder when the download completes" });
        var exitCheckBox = StyleCheckBox(new CheckBox { Text = "Exit QuickByte when the download completes", Enabled = false });
        var soundCheckBox = StyleCheckBox(new CheckBox { Text = "Play a sound when the download completes", Enabled = false });

        openFolderCheckBox.CheckedChanged += (_, _) => _openOnCompleteRequested = openFolderCheckBox.Checked;

        var note = new Label
        {
            Text = "Greyed-out options are placeholders and are not wired up yet.",
            AutoSize = true,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            Margin = new Padding(0, 18, 0, 0)
        };

        stack.Controls.Add(openFolderCheckBox);
        stack.Controls.Add(exitCheckBox);
        stack.Controls.Add(soundCheckBox);
        stack.Controls.Add(note);
        page.Controls.Add(stack);
    }

    private static CheckBox StyleCheckBox(CheckBox box)
    {
        box.AutoSize = true;
        box.Font = Theme.Ui;
        box.ForeColor = Theme.Text;
        box.BackColor = Theme.Surface;
        box.Margin = new Padding(0, 6, 0, 6);
        box.FlatStyle = FlatStyle.Standard;
        return box;
    }

    private BufferedListView BuildConnectionsListView()
    {
        var listView = new BufferedListView { Dock = DockStyle.Fill, MultiSelect = false };
        listView.SetRowHeight(24);
        listView.Columns.Add("#", 40);
        listView.Columns.Add("Downloaded", 110);
        listView.Columns.Add("Progress", 190);
        listView.Columns.Add("Info", 140);
        listView.DrawSubItem += ConnectionsListView_DrawSubItem;
        return listView;
    }

    private void ConnectionsListView_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item?.Tag is not ConnectionInfo info) { e.DrawDefault = true; return; }

        bool selected = e.Item.Selected;

        if (e.ColumnIndex == ConnectionColumnProgress)
        {
            ListViewProgressPainter.Draw(e.Graphics, e.Bounds, _connectionProgress.Displayed(info.ConnectionId),
                ColorForConnection(info.Status),
                _connectionsListView.RowForeColor(e.ItemIndex, selected, Theme.Text));
            return;
        }

        var color = e.ColumnIndex switch
        {
            ConnectionColumnId => Theme.TextMuted,
            ConnectionColumnInfo => ColorForConnection(info.Status),
            _ => Theme.Text
        };
        var font = e.ColumnIndex == ConnectionColumnInfo ? Theme.UiSmall : Theme.Ui;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                    (e.ColumnIndex == ConnectionColumnDownloaded ? TextFormatFlags.Right : TextFormatFlags.Left);
        var bounds = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 14, e.Bounds.Height);

        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, font, bounds,
            _connectionsListView.RowForeColor(e.ItemIndex, selected, color), flags);
    }

    private static Color ColorForConnection(ConnectionStatus status) => status switch
    {
        ConnectionStatus.Finished => Theme.Success,
        ConnectionStatus.Failed => Theme.Danger,
        ConnectionStatus.Paused => Theme.Warning,
        ConnectionStatus.Idle => Theme.TextMuted,
        _ => Theme.Accent
    };

    private Panel BuildButtonsPanel()
    {
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Theme.HeaderBack, Padding = new Padding(14, 12, 14, 12) };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, 0, 0, panel.Width, 0);
        };

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            BackColor = Theme.HeaderBack,
            WrapContents = false
        };
        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            BackColor = Theme.HeaderBack,
            WrapContents = false
        };

        _cancelButton = Theme.StyleButton(new Button { Text = "Cancel" });
        _cancelButton.Click += (_, _) => _downloadManager.Stop(_item.Id);

        _pauseResumeButton = Theme.StyleButton(new Button { Text = "Pause" }, primary: true);
        _pauseResumeButton.Click += async (_, _) => await OnPauseResumeClickedAsync();

        _retryButton = Theme.StyleButton(new Button { Text = "Retry" });
        _retryButton.Click += async (_, _) => await _downloadManager.RetryAsync(_item.Id);

        _openFolderButton = Theme.StyleButton(new Button { Text = "Open Folder", Width = 110 });
        _openFolderButton.Click += (_, _) => FileLauncher.RevealInExplorer(_item.FullPath, _item.SaveFolder);

        var closeButton = Theme.StyleButton(new Button { Text = "Close" });
        closeButton.Click += (_, _) => Close();

        right.Controls.Add(_pauseResumeButton);
        right.Controls.Add(_cancelButton);
        right.Controls.Add(_retryButton);
        right.Controls.Add(closeButton);
        left.Controls.Add(_openFolderButton);

        panel.Controls.Add(right);
        panel.Controls.Add(left);
        return panel;
    }

    private void ToggleDetailsSection()
    {
        _detailsExpanded = !_detailsExpanded;
        _detailsPanel.Visible = _detailsExpanded;
        _hideDetailsButton.Text = _detailsExpanded ? "▲  Hide details" : "▼  Show details";
        Height = _detailsExpanded ? Math.Max(Height, 620) : 320;
    }

    private async Task OnPauseResumeClickedAsync()
    {
        if (_item.Status is DownloadStatus.Downloading or DownloadStatus.Connecting)
            _downloadManager.Pause(_item.Id);
        else
            await _downloadManager.ResumeAsync(_item.Id);
    }

    // ------------------------------------------------------ Manager wiring --

    private void WireEvents()
    {
        _downloadManager.ProgressChanged += OnProgressChanged;
        _downloadManager.StatusChanged += OnStatusChanged;
        _downloadManager.ConnectionsChanged += OnConnectionsChanged;

        FormClosed += (_, _) =>
        {
            _downloadManager.ProgressChanged -= OnProgressChanged;
            _downloadManager.StatusChanged -= OnStatusChanged;
            _downloadManager.ConnectionsChanged -= OnConnectionsChanged;
        };
    }

    private void OnProgressChanged(object? sender, DownloadProgressEventArgs e)
    {
        if (e.DownloadId != _item.Id) return;
        RefreshDynamicFields(progress: e);
    }

    private void OnStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        if (e.DownloadId != _item.Id) return;

        RefreshDynamicFields();
        UpdateButtonsForStatus(e.NewStatus);

        if (e.NewStatus == DownloadStatus.Failed && !string.IsNullOrEmpty(e.ErrorMessage))
        {
            _statusValueLabel.Text = "Failed";
            _statusValueLabel.ForeColor = Theme.Danger;
            _toolTip.SetToolTip(_statusValueLabel, e.ErrorMessage);
        }

        if (e.NewStatus == DownloadStatus.Completed && _openOnCompleteRequested)
            FileLauncher.RevealInExplorer(_item.FullPath, _item.SaveFolder);
    }

    private void OnConnectionsChanged(object? sender, ConnectionsSnapshotEventArgs e)
    {
        if (e.DownloadId != _item.Id) return;
        RefreshConnectionsList(e.Connections);
        _segmentsBar.UpdateData(e.Connections, _item.TotalBytes);
    }

    // ------------------------------------------------------------ Rendering --

    private void RefreshStaticFields()
    {
        _resumeValueLabel.Text = _item.SupportsResume ? "Yes" : "No";
        _resumeValueLabel.ForeColor = _item.SupportsResume ? Theme.Success : Theme.Warning;
        _connectionsValueLabel.Text = _item.ConnectionsCount.ToString();
        UpdateButtonsForStatus(_item.Status);
    }

    private void RefreshDynamicFields(DownloadProgressEventArgs? progress = null, bool immediate = false)
    {
        long downloaded = progress?.DownloadedBytes ?? _item.DownloadedBytes;
        long total = progress?.TotalBytes ?? _item.TotalBytes;
        double speed = progress?.SpeedBytesPerSecond ?? _item.CurrentSpeedBytesPerSecond;
        var eta = progress?.EstimatedTimeRemaining ?? _item.EstimatedTimeRemaining;
        bool merging = _item.Status == DownloadStatus.Merging;

        _sizeValueLabel.Text = total > 0 ? ByteFormatter.FormatBytes(total) : "Unknown";
        double percentage = total > 0 ? Math.Min(100.0, downloaded * 100.0 / total) : 0;
        _downloadedValueLabel.Text = $"{ByteFormatter.FormatBytes(downloaded)}  ({ByteFormatter.FormatPercentage(percentage)})";
        _speedValueLabel.Text = merging || speed <= 0 ? "—" : ByteFormatter.FormatSpeed(speed);
        _etaValueLabel.Text = merging ? "—" : ByteFormatter.FormatEta(eta);

        _statusValueLabel.Text = StatusText(_item.Status);
        _statusValueLabel.ForeColor = StatusColor(_item.Status);

        _overallProgressBar.BarColor = ListViewProgressPainter.ColorForStatus(
            failed: _item.Status is DownloadStatus.Failed or DownloadStatus.Cancelled,
            paused: _item.Status is DownloadStatus.Paused,
            completed: _item.Status is DownloadStatus.Completed);

        // While merging the bar stays full and reports merge progress as text —
        // the bytes are already on disk, so rewinding the bar would be a lie.
        _overallProgressBar.OverlayText = merging
            ? $"Merging file parts…  {ByteFormatter.FormatPercentage(progress?.MergePercentage ?? _item.MergeProgressPercentage)}"
            : null;
        _overallProgressBar.SetValue(percentage, immediate);

        Text = _item.Status == DownloadStatus.Completed
            ? $"Complete — {_item.FileName}"
            : $"{ByteFormatter.FormatPercentage(percentage)}  {_item.FileName}";
    }

    private static string StatusText(DownloadStatus status) => status switch
    {
        DownloadStatus.Connecting => "Connecting…",
        DownloadStatus.Downloading => "Receiving data…",
        DownloadStatus.Merging => "Merging file parts…",
        DownloadStatus.Paused => "Paused",
        DownloadStatus.Completed => "Complete",
        DownloadStatus.Failed => "Failed",
        DownloadStatus.Cancelled => "Cancelled",
        _ => "Queued"
    };

    private static Color StatusColor(DownloadStatus status) => status switch
    {
        DownloadStatus.Completed => Theme.Success,
        DownloadStatus.Failed or DownloadStatus.Cancelled => Theme.Danger,
        DownloadStatus.Paused => Theme.Warning,
        _ => Theme.Accent
    };

    private void RefreshConnectionsList(IReadOnlyList<ConnectionInfo> connections)
    {
        if (connections.Count == 0) return;

        bool added = false;
        foreach (var info in connections.OrderBy(c => c.ConnectionId))
        {
            if (!_connectionRowsById.TryGetValue(info.ConnectionId, out var row))
            {
                row = new ListViewItem(new[] { (info.ConnectionId + 1).ToString(), string.Empty, string.Empty, string.Empty });
                _connectionsListView.Items.Add(row);
                _connectionRowsById[info.ConnectionId] = row;
                added = true;
            }

            row.Tag = info;
            // Only touched when the text actually differs — assigning identical
            // text still invalidates the row, which is what made this list blink.
            BufferedListView.SetSubItemText(row, ConnectionColumnDownloaded, ByteFormatter.FormatBytes(info.BytesDownloaded));
            BufferedListView.SetSubItemText(row, ConnectionColumnInfo, FormatConnectionInfo(info));
            _connectionProgress.SetTarget(info.ConnectionId, info.ProgressPercentage);
        }

        if (added) _connectionsListView.Invalidate();
        if (!_animationTimer.Enabled) _animationTimer.Start();
    }

    private void OnAnimationTick()
    {
        var moved = _connectionProgress.Advance();
        if (moved.Count == 0)
        {
            _animationTimer.Stop();
            return;
        }

        // Cell-scoped for the same reason MainForm's tick is: the connection
        // rows animate continuously, and only the progress cell changes.
        foreach (var id in moved)
        {
            if (_connectionRowsById.TryGetValue(id, out var row))
                _connectionsListView.InvalidateCell(row, ConnectionColumnProgress);
        }
    }

    private static string FormatConnectionInfo(ConnectionInfo info) => info.Status switch
    {
        ConnectionStatus.SendingRequest => info.RetryCount > 0 ? $"Retrying (attempt {info.RetryCount})…" : "Sending GET…",
        ConnectionStatus.ReceivingData => "Receiving data…",
        ConnectionStatus.Finished => "Finished",
        ConnectionStatus.Failed => $"Failed — {info.LastError ?? "error"}",
        ConnectionStatus.Paused => "Paused",
        _ => "Idle"
    };

    private void InitializeComponent()
    {
        SuspendLayout();
        // 
        // DownloadDetailsForm
        // 
        ClientSize = new Size(282, 253);
        MaximizeBox = false;
        Name = "DownloadDetailsForm";
        ResumeLayout(false);

    }

    private void UpdateButtonsForStatus(DownloadStatus status)
    {
        bool isActive = status is DownloadStatus.Downloading or DownloadStatus.Connecting;
        _pauseResumeButton.Text = isActive ? "Pause" : "Resume";
        _pauseResumeButton.Enabled = status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Paused or DownloadStatus.Queued;
        _cancelButton.Enabled = status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Paused;
        _retryButton.Enabled = status is DownloadStatus.Failed or DownloadStatus.Cancelled;
        _openFolderButton.Enabled = status == DownloadStatus.Completed || File.Exists(_item.FullPath);
    }
}
