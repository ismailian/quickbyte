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
/// Main application window: menu bar + icon toolbar on top, a category tree
/// sidebar on the left, and a downloads ListView on the right — mirroring
/// IDM's layout. Subscribes directly to <see cref="IDownloadManager"/>'s
/// aggregated events — the single source of truth shared with every open
/// <see cref="DownloadDetailsForm"/> — and updates ListView rows in place
/// (O(1) lookup via a Guid->row map) rather than rebinding the whole list,
/// keeping updates smooth even with many simultaneous downloads.
///
/// Progress cells are painted from <see cref="ProgressAnimator{TKey}"/> rather
/// than straight from the last event, so the bars glide between the engine's
/// ~100 ms samples instead of stepping.
/// </summary>
public sealed class MainForm : Form
{
    private const int ColumnName = 0;
    private const int ColumnSize = 1;
    private const int ProgressColumnIndex = 2;
    private const int ColumnSpeed = 3;
    private const int ColumnEta = 4;
    private const int ColumnStatus = 5;
    private const int ColumnDescription = 6;

    private const int RowHeight = 26;

    private readonly IDownloadManager _downloadManager;
    private readonly ISettingsService _settingsService;

    private readonly Dictionary<Guid, ListViewItem> _rowsById = new();
    private readonly List<ListViewItem> _orderedRows = new();
    private readonly Dictionary<Guid, DownloadDetailsForm> _openDetailForms = new();
    private readonly Dictionary<FileCategory, Bitmap> _categoryIcons = new();
    private readonly ProgressAnimator<Guid> _progress = new();
    private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = ProgressAnimation.FrameIntervalMilliseconds };

    private TreeView _sidebar = null!;
    private BufferedListView _listView = null!;
    private ToolStrip _toolStrip = null!;
    private Panel _sidebarPanel = null!;
    private Splitter _sidebarSplitter = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private ToolStripStatusLabel _speedLabel = null!;
    private Func<DownloadItem, bool>? _activeFilter;

    private ToolStripButton _resumeButton = null!;
    private ToolStripButton _pauseButton = null!;
    private ToolStripButton _stopAllButton = null!;
    private ToolStripButton _deleteButton = null!;
    private ToolStripButton _deleteCompletedButton = null!;
    private ToolStripButton _propertiesButton = null!;

    public MainForm(IDownloadManager downloadManager, ISettingsService settingsService)
    {
        _downloadManager = downloadManager;
        _settingsService = settingsService;

        BuildUi();
        WireManagerEvents();
        PopulateFromExistingDownloads();
        UpdateActionButtonsState();

        _animationTimer.Tick += (_, _) => OnAnimationTick();
        FormClosed += (_, _) => _animationTimer.Dispose();
    }

    // ---------------------------------------------------------------- UI --

    private void BuildUi()
    {
        Text = $"QuickByte Download Manager {AppVersion.Display}";
        Width = 1200;
        Height = 700;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 540);
        BackColor = Theme.Window;
        Font = Theme.Ui;
        Icon = BrandIcon.App;

        var menuStrip = BuildMenuStrip();
        _toolStrip = BuildToolStrip();
        _sidebarPanel = BuildSidebar();
        _listView = BuildListView();
        var statusStrip = BuildStatusStrip();

        // A 1px top inset gives the list a hairline frame instead of the heavy
        // 3D border WinForms draws by default.
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 1, 0, 0), BackColor = Theme.Border };
        listHost.Controls.Add(_listView);

        _sidebarSplitter = new Splitter
        {
            Dock = DockStyle.Left,
            Width = 5,
            BackColor = Theme.Window,
            MinExtra = 420,
            MinSize = 150
        };

        // Docked children are laid out last-added-first, so the Fill host goes
        // in before the splitter and the sidebar that must sit outside it.
        Controls.Add(listHost);
        Controls.Add(_sidebarSplitter);
        Controls.Add(_sidebarPanel);
        Controls.Add(statusStrip);
        Controls.Add(_toolStrip);
        Controls.Add(menuStrip);
        MainMenuStrip = menuStrip;
    }

    private MenuStrip BuildMenuStrip()
    {
        var menu = new MenuStrip
        {
            RenderMode = ToolStripRenderMode.Professional,
            Renderer = new FlatToolStripRenderer(),
            BackColor = Theme.Chrome,
            ForeColor = Theme.Text,
            Font = Theme.Ui,
            Padding = new Padding(6, 2, 0, 2)
        };

        var tasksMenu = new ToolStripMenuItem("&Tasks");
        tasksMenu.DropDownItems.Add("Add &URL...", IconFactory.AddUrl(16), (_, _) => OnAddDownloadClicked());
        tasksMenu.DropDownItems.Add(new ToolStripSeparator());
        tasksMenu.DropDownItems.Add("&Resume", IconFactory.Resume(16), async (_, _) => await OnResumeClickedAsync());
        tasksMenu.DropDownItems.Add("&Pause", IconFactory.Pause(16), (_, _) => OnPauseClicked());
        tasksMenu.DropDownItems.Add("Stop &All", IconFactory.StopAll(16), (_, _) => OnStopAllClicked());
        tasksMenu.DropDownItems.Add(new ToolStripSeparator());
        tasksMenu.DropDownItems.Add("&Delete", IconFactory.Delete(16), (_, _) => OnRemoveClicked());
        tasksMenu.DropDownItems.Add("Delete &Completed", IconFactory.DeleteCompleted(16), (_, _) => OnDeleteCompletedClicked());
        tasksMenu.DropDownItems.Add(new ToolStripSeparator());
        tasksMenu.DropDownItems.Add("&Options...", IconFactory.Settings(16), (_, _) => OnSettingsClicked());

        var fileMenu = new ToolStripMenuItem("&File");
        fileMenu.DropDownItems.Add("&Open File", IconFactory.OpenFile(16), (_, _) => OpenSelectedFile());
        fileMenu.DropDownItems.Add("Open Containing &Folder", IconFactory.OpenFolder(16), (_, _) => OnOpenFolderClicked());
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add("E&xit", null, (_, _) => Close());

        var downloadsMenu = new ToolStripMenuItem("&Downloads");
        downloadsMenu.DropDownItems.Add("&Properties...", IconFactory.Properties(16), (_, _) => OpenSelectedDetailsWindow());
        downloadsMenu.DropDownItems.Add("&Retry", IconFactory.Retry(16), async (_, _) => await OnRetryClickedAsync());

        var viewMenu = new ToolStripMenuItem("&View");
        var toolbarToggle = new ToolStripMenuItem("&Toolbar") { CheckOnClick = true, Checked = true };
        toolbarToggle.Click += (_, _) => _toolStrip.Visible = toolbarToggle.Checked;
        var sidebarToggle = new ToolStripMenuItem("&Categories Panel") { CheckOnClick = true, Checked = true };
        sidebarToggle.Click += (_, _) => SetSidebarVisible(sidebarToggle.Checked);
        viewMenu.DropDownItems.Add(toolbarToggle);
        viewMenu.DropDownItems.Add(sidebarToggle);

        var helpMenu = new ToolStripMenuItem("&Help");
        helpMenu.DropDownItems.Add("&About QuickByte...", IconFactory.Properties(16), (_, _) => ShowAboutDialog());

        menu.Items.AddRange(new ToolStripItem[] { fileMenu, tasksMenu, downloadsMenu, viewMenu, helpMenu });
        return menu;
    }

    private void ShowAboutDialog()
    {
        using var about = new AboutForm();
        about.ShowDialog(this);
    }

    private ToolStrip BuildToolStrip()
    {
        ToolStripButton MakeButton(string text, Bitmap icon, EventHandler onClick, string? tooltip = null)
        {
            var button = new ToolStripButton(text, icon)
            {
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                TextImageRelation = TextImageRelation.ImageAboveText,
                TextAlign = ContentAlignment.BottomCenter,
                ImageAlign = ContentAlignment.TopCenter,
                Font = Theme.UiSmall,
                Margin = new Padding(2, 0, 2, 0),
                Padding = new Padding(6, 4, 6, 2),
                ToolTipText = tooltip ?? text
            };
            button.Click += onClick;
            return button;
        }

        var addButton = MakeButton("Add URL", IconFactory.AddUrl(28), (_, _) => OnAddDownloadClicked(), "Add a new download (Ctrl+N)");
        _resumeButton = MakeButton("Resume", IconFactory.Resume(28), async (_, _) => await OnResumeClickedAsync());
        _pauseButton = MakeButton("Pause", IconFactory.Pause(28), (_, _) => OnPauseClicked());
        _stopAllButton = MakeButton("Stop All", IconFactory.StopAll(28), (_, _) => OnStopAllClicked());
        _deleteButton = MakeButton("Delete", IconFactory.Delete(28), (_, _) => OnRemoveClicked());
        _deleteCompletedButton = MakeButton("Delete Completed", IconFactory.DeleteCompleted(28), (_, _) => OnDeleteCompletedClicked());
        _propertiesButton = MakeButton("Properties", IconFactory.Properties(28), (_, _) => OpenSelectedDetailsWindow());
        var settingsButton = MakeButton("Options", IconFactory.Settings(28), (_, _) => OnSettingsClicked());

        var strip = new ToolStrip
        {
            ImageScalingSize = new Size(28, 28),
            Padding = new Padding(6, 4, 6, 4),
            GripStyle = ToolStripGripStyle.Hidden,
            RenderMode = ToolStripRenderMode.Professional,
            Renderer = new FlatToolStripRenderer(),
            BackColor = Theme.Chrome,
            AutoSize = true
        };
        strip.Items.AddRange(new ToolStripItem[]
        {
            addButton, new ToolStripSeparator(),
            _resumeButton, _pauseButton, _stopAllButton, new ToolStripSeparator(),
            _deleteButton, _deleteCompletedButton, new ToolStripSeparator(),
            _propertiesButton, settingsButton
        });
        return strip;
    }

    private StatusStrip BuildStatusStrip()
    {
        var strip = new StatusStrip
        {
            RenderMode = ToolStripRenderMode.Professional,
            Renderer = new FlatToolStripRenderer(),
            BackColor = Theme.Chrome,
            SizingGrip = true,
            Padding = new Padding(8, 0, 12, 0)
        };
        _statusLabel = new ToolStripStatusLabel
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall
        };
        _speedLabel = new ToolStripStatusLabel
        {
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Theme.Accent,
            Font = Theme.UiSmallBold
        };
        strip.Items.Add(_statusLabel);
        strip.Items.Add(_speedLabel);
        return strip;
    }

    private Panel BuildSidebar()
    {
        var panel = new Panel { Dock = DockStyle.Left, Width = 214, BackColor = Theme.Sidebar, Padding = new Padding(0, 0, 1, 0) };

        var header = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Theme.Sidebar };
        var headerLabel = new Label
        {
            Text = "CATEGORIES",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 0, 0),
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmallBold
        };
        var closeButton = new Label
        {
            Text = "✕",
            Dock = DockStyle.Right,
            Width = 28,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Theme.TextMuted,
            Font = Theme.UiSmall,
            Cursor = Cursors.Hand
        };
        closeButton.Click += (_, _) => SetSidebarVisible(false);
        closeButton.MouseEnter += (_, _) => closeButton.ForeColor = Theme.Danger;
        closeButton.MouseLeave += (_, _) => closeButton.ForeColor = Theme.TextMuted;
        header.Controls.Add(headerLabel);
        header.Controls.Add(closeButton);

        _sidebar = BuildSidebarTree();

        panel.Controls.Add(_sidebar);
        panel.Controls.Add(Theme.Separator());
        panel.Controls.Add(header);
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, panel.Width - 1, 0, panel.Width - 1, panel.Height);
        };
        return panel;
    }

    private TreeView BuildSidebarTree()
    {
        var treeImages = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        treeImages.Images.Add("all", IconFactory.Folder(16, Theme.AccentLight));
        treeImages.Images.Add("unfinished", IconFactory.StatusDot(Theme.Accent));
        treeImages.Images.Add("finished", IconFactory.StatusDot(Theme.Success));
        treeImages.Images.Add("failed", IconFactory.StatusDot(Theme.Danger));
        treeImages.Images.Add("queues", IconFactory.Queue(16));
        foreach (FileCategory category in Enum.GetValues<FileCategory>())
            treeImages.Images.Add(category.ToString(), IconFactory.CategoryIcon(category));

        var tree = new TreeView
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            BackColor = Theme.Sidebar,
            ForeColor = Theme.Text,
            LineColor = Theme.BorderStrong,
            Font = Theme.Ui,
            ImageList = treeImages,
            FullRowSelect = true,
            HideSelection = false,
            ShowLines = false,
            ShowRootLines = true,
            ShowPlusMinus = true,
            ItemHeight = 24,
            Indent = 18
        };

        var allNode = new TreeNode("All Downloads") { Tag = (Func<DownloadItem, bool>?)null, ImageKey = "all", SelectedImageKey = "all" };
        foreach (FileCategory category in Enum.GetValues<FileCategory>())
        {
            string label = category == FileCategory.Other ? "Other" : category.ToString();
            var categoryNode = new TreeNode(label)
            {
                Tag = (Func<DownloadItem, bool>)(item => FileCategoryHelper.GetCategory(item.FileName) == category),
                ImageKey = category.ToString(),
                SelectedImageKey = category.ToString()
            };
            allNode.Nodes.Add(categoryNode);
        }

        var unfinishedNode = new TreeNode("Unfinished")
        {
            Tag = (Func<DownloadItem, bool>)(item => item.Status is DownloadStatus.Queued or DownloadStatus.Connecting
                or DownloadStatus.Downloading or DownloadStatus.Paused or DownloadStatus.Merging),
            ImageKey = "unfinished",
            SelectedImageKey = "unfinished"
        };
        var finishedNode = new TreeNode("Finished")
        {
            Tag = (Func<DownloadItem, bool>)(item => item.Status == DownloadStatus.Completed),
            ImageKey = "finished",
            SelectedImageKey = "finished"
        };
        var failedNode = new TreeNode("Failed")
        {
            Tag = (Func<DownloadItem, bool>)(item => item.Status is DownloadStatus.Failed or DownloadStatus.Cancelled),
            ImageKey = "failed",
            SelectedImageKey = "failed"
        };
        var queuesNode = new TreeNode("Queues")
        {
            Tag = (Func<DownloadItem, bool>)(item => item.Status == DownloadStatus.Queued),
            ImageKey = "queues",
            SelectedImageKey = "queues"
        };

        tree.Nodes.AddRange(new[] { allNode, unfinishedNode, finishedNode, failedNode, queuesNode });
        allNode.Expand();

        tree.AfterSelect += (_, e) =>
        {
            _activeFilter = e.Node?.Tag as Func<DownloadItem, bool>;
            ApplyFilter();
        };
        tree.SelectedNode = allNode;

        return tree;
    }

    private void SetSidebarVisible(bool visible)
    {
        _sidebarPanel.Visible = visible;
        _sidebarSplitter.Visible = visible;
    }

    private BufferedListView BuildListView()
    {
        var listView = new BufferedListView { Dock = DockStyle.Fill, MultiSelect = true };
        listView.SetRowHeight(RowHeight);

        listView.Columns.Add("File Name", 228);
        listView.Columns.Add("Size", 80);
        listView.Columns.Add("Progress", 118);
        listView.Columns.Add("Transfer Rate", 116);
        listView.Columns.Add("Time Left", 96);
        listView.Columns.Add("Status", 122);
        listView.Columns.Add("Description", 140);

        listView.DrawSubItem += ListView_DrawSubItem;
        listView.MouseDoubleClick += (_, _) => OnRowActivated();
        listView.SelectedIndexChanged += (_, _) => UpdateActionButtonsState();
        listView.KeyDown += ListView_KeyDown;
        listView.ContextMenuStrip = BuildRowContextMenu();

        return listView;
    }

    private void ListView_KeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Delete:
                OnRemoveClicked();
                break;
            case Keys.Enter:
                OnRowActivated();
                break;
        }
    }

    private ContextMenuStrip BuildRowContextMenu()
    {
        var menu = new ContextMenuStrip
        {
            RenderMode = ToolStripRenderMode.Professional,
            Renderer = new FlatToolStripRenderer(),
            BackColor = Theme.Surface,
            Font = Theme.Ui,
            ShowImageMargin = true
        };
        menu.Items.Add("Open File", IconFactory.OpenFile(16), (_, _) => OpenSelectedFile());
        menu.Items.Add("Open Containing Folder", IconFactory.OpenFolder(16), (_, _) => OnOpenFolderClicked());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Resume", IconFactory.Resume(16), async (_, _) => await OnResumeClickedAsync());
        menu.Items.Add("Pause", IconFactory.Pause(16), (_, _) => OnPauseClicked());
        menu.Items.Add("Stop", IconFactory.Stop(16), (_, _) => OnStopClicked());
        menu.Items.Add("Retry", IconFactory.Retry(16), async (_, _) => await OnRetryClickedAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Remove", IconFactory.Delete(16), (_, _) => OnRemoveClicked());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Properties...", IconFactory.Properties(16), (_, _) => OpenSelectedDetailsWindow());
        menu.Opening += (_, _) => UpdateActionButtonsState();
        return menu;
    }

    // ------------------------------------------------------------ Painting --

    private void ListView_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item?.Tag is not DownloadItem item) { e.DrawDefault = true; return; }

        bool selected = e.Item.Selected;
        var bounds = e.Bounds;

        if (e.ColumnIndex == ProgressColumnIndex)
        {
            var color = ListViewProgressPainter.ColorForStatus(
                failed: item.Status is DownloadStatus.Failed or DownloadStatus.Cancelled,
                paused: item.Status is DownloadStatus.Paused,
                completed: item.Status is DownloadStatus.Completed);

            double percentage = item.Status == DownloadStatus.Completed ? 100 : _progress.Displayed(item.Id);
            ListViewProgressPainter.Draw(e.Graphics, bounds, percentage, color,
                _listView.RowForeColor(e.ItemIndex, selected, Theme.Text));
            return;
        }

        var textBounds = new Rectangle(bounds.X + 7, bounds.Y, bounds.Width - 10, bounds.Height);
        var font = Theme.Ui;
        var foreColor = Theme.Text;

        if (e.ColumnIndex == ColumnName)
        {
            var icon = CategoryIcon(FileCategoryHelper.GetCategory(item.FileName));
            int iconY = bounds.Y + (bounds.Height - 16) / 2;
            e.Graphics.DrawImage(icon, bounds.X + 7, iconY, 16, 16);
            textBounds = new Rectangle(bounds.X + 29, bounds.Y, bounds.Width - 32, bounds.Height);
        }
        else if (e.ColumnIndex == ColumnStatus)
        {
            foreColor = StatusColor(item.Status);
            font = Theme.UiSmallBold;
        }
        else if (e.ColumnIndex is ColumnSpeed or ColumnEta or ColumnSize)
        {
            foreColor = Theme.TextMuted;
        }
        else if (e.ColumnIndex == ColumnDescription)
        {
            foreColor = item.Status == DownloadStatus.Failed ? Theme.Danger : Theme.TextMuted;
            font = Theme.UiSmall;
        }

        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, font, textBounds,
            _listView.RowForeColor(e.ItemIndex, selected, foreColor),
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private Bitmap CategoryIcon(FileCategory category)
    {
        if (!_categoryIcons.TryGetValue(category, out var icon))
        {
            icon = IconFactory.CategoryIcon(category);
            _categoryIcons[category] = icon;
        }
        return icon;
    }

    private static Color StatusColor(DownloadStatus status) => status switch
    {
        DownloadStatus.Completed => Theme.Success,
        DownloadStatus.Failed or DownloadStatus.Cancelled => Theme.Danger,
        DownloadStatus.Paused => Theme.Warning,
        DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Merging => Theme.Accent,
        _ => Theme.TextMuted
    };

    private static string StatusText(DownloadItem item) => item.Status switch
    {
        DownloadStatus.Connecting => "Connecting",
        DownloadStatus.Downloading => "Downloading",
        DownloadStatus.Merging => $"Merging {item.MergeProgressPercentage:0}%",
        DownloadStatus.Completed => "Complete",
        DownloadStatus.Cancelled => "Cancelled",
        _ => item.Status.ToString()
    };

    // ------------------------------------------------------ Manager wiring --

    private void WireManagerEvents()
    {
        _downloadManager.DownloadListChanged += OnDownloadListChanged;
        _downloadManager.ProgressChanged += OnProgressChanged;
        _downloadManager.StatusChanged += OnStatusChanged;

        FormClosed += (_, _) =>
        {
            _downloadManager.DownloadListChanged -= OnDownloadListChanged;
            _downloadManager.ProgressChanged -= OnProgressChanged;
            _downloadManager.StatusChanged -= OnStatusChanged;
        };
    }

    private void PopulateFromExistingDownloads()
    {
        foreach (var item in _downloadManager.Downloads)
            AddRow(item, atTop: false);
        ApplyFilter();
        UpdateStatusBar();
    }

    private void OnDownloadListChanged(object? sender, DownloadListChangedEventArgs e)
    {
        if (e.ChangeType == DownloadListChangeType.Added)
        {
            AddRow(e.Item, atTop: true);
            SetRowVisible(_rowsById[e.Item.Id], PassesFilter(e.Item));

            if (_settingsService.Current.AutoOpenDetailsWindow)
                OpenDetailsWindow(e.Item);
        }
        else
        {
            if (_rowsById.Remove(e.Item.Id, out var row))
            {
                _orderedRows.Remove(row);
                _listView.Items.Remove(row);
            }
            _progress.Remove(e.Item.Id);
            if (_openDetailForms.Remove(e.Item.Id, out var form))
                form.Close();
        }
        UpdateStatusBar();
        UpdateActionButtonsState();
    }

    private void OnProgressChanged(object? sender, DownloadProgressEventArgs e)
    {
        if (!_rowsById.TryGetValue(e.DownloadId, out var row)) return;
        if (row.Tag is not DownloadItem item) return;

        BufferedListView.SetSubItemText(row, ColumnSize, ByteFormatter.FormatBytes(item.TotalBytes));
        BufferedListView.SetSubItemText(row, ColumnSpeed, e.MergePercentage is null ? ByteFormatter.FormatSpeed(e.SpeedBytesPerSecond) : "—");
        BufferedListView.SetSubItemText(row, ColumnEta, e.MergePercentage is null ? ByteFormatter.FormatEta(e.EstimatedTimeRemaining) : "—");
        if (e.MergePercentage is not null)
            BufferedListView.SetSubItemText(row, ColumnStatus, StatusText(item));

        _progress.SetTarget(item.Id, item.ProgressPercentage);
        EnsureAnimating();
        UpdateStatusBar();
    }

    private void OnStatusChanged(object? sender, DownloadStatusChangedEventArgs e)
    {
        if (!_rowsById.TryGetValue(e.DownloadId, out var row)) return;
        if (row.Tag is not DownloadItem item) return;

        BufferedListView.SetSubItemText(row, ColumnStatus, StatusText(item));
        BufferedListView.SetSubItemText(row, ColumnSize, ByteFormatter.FormatBytes(item.TotalBytes));
        BufferedListView.SetSubItemText(row, ColumnDescription, DescriptionFor(item));

        if (e.NewStatus is DownloadStatus.Completed)
        {
            _progress.SetTarget(item.Id, 100);
            BufferedListView.SetSubItemText(row, ColumnSpeed, "—");
            BufferedListView.SetSubItemText(row, ColumnEta, "—");
        }
        else if (e.NewStatus is DownloadStatus.Paused or DownloadStatus.Failed or DownloadStatus.Cancelled)
        {
            BufferedListView.SetSubItemText(row, ColumnSpeed, "—");
            BufferedListView.SetSubItemText(row, ColumnEta, "—");
        }

        EnsureAnimating();
        if (row.ListView is not null) _listView.Invalidate(row.Bounds);

        SetRowVisible(row, PassesFilter(item));
        UpdateActionButtonsState();
        UpdateStatusBar();

        if (e.NewStatus == DownloadStatus.Completed)
            OnDownloadCompleted(item);
    }

    private void OnDownloadCompleted(DownloadItem item)
    {
        // IDM-style hand-off: the progress window makes way for a completion
        // window rather than leaving two windows describing the same download.
        if (_openDetailForms.Remove(item.Id, out var detailsForm))
            detailsForm.Close();

        if (!_settingsService.Current.ShowCompletionWindow) return;

        var completeForm = new DownloadCompleteForm(item, _settingsService);
        completeForm.Show(this);
    }

    // --------------------------------------------------------------- Rows --

    private void AddRow(DownloadItem item, bool atTop)
    {
        // Speed/ETA are meaningless for anything not actively transferring —
        // persisted rows would otherwise reload showing the rate they finished at.
        bool live = item.Status is DownloadStatus.Downloading or DownloadStatus.Connecting;

        var row = new ListViewItem(item.FileName) { Tag = item };
        row.SubItems.Add(ByteFormatter.FormatBytes(item.TotalBytes));
        row.SubItems.Add(string.Empty); // progress cell — custom painted
        row.SubItems.Add(live && item.CurrentSpeedBytesPerSecond > 0 ? ByteFormatter.FormatSpeed(item.CurrentSpeedBytesPerSecond) : "—");
        row.SubItems.Add(live ? ByteFormatter.FormatEta(item.EstimatedTimeRemaining) : "—");
        row.SubItems.Add(StatusText(item));
        row.SubItems.Add(DescriptionFor(item));

        _rowsById[item.Id] = row;
        if (atTop) _orderedRows.Insert(0, row); else _orderedRows.Add(row);
        _progress.SetTarget(item.Id, item.ProgressPercentage, immediate: true);
    }

    private static string DescriptionFor(DownloadItem item)
    {
        if (item.Status == DownloadStatus.Failed && !string.IsNullOrWhiteSpace(item.ErrorMessage))
            return item.ErrorMessage!;
        if (item.Status == DownloadStatus.Completed && item.CompletedAt is not null)
            return item.CompletedAt.Value.LocalDateTime.ToString("g");
        return string.IsNullOrEmpty(item.ContentType) ? "—" : item.ContentType;
    }

    private bool PassesFilter(DownloadItem item) => _activeFilter is null || _activeFilter(item);

    /// <summary>
    /// Adds/removes a single row instead of rebuilding the whole list when one
    /// download changes category — clearing and re-adding every row on each
    /// status change is what made the list flash.
    /// </summary>
    private void SetRowVisible(ListViewItem row, bool visible)
    {
        bool currentlyVisible = row.ListView is not null;
        if (visible == currentlyVisible) return;

        if (!visible)
        {
            _listView.Items.Remove(row);
            return;
        }

        int insertAt = 0;
        foreach (var candidate in _orderedRows)
        {
            if (ReferenceEquals(candidate, row)) break;
            if (candidate.ListView is not null) insertAt++;
        }
        _listView.Items.Insert(Math.Min(insertAt, _listView.Items.Count), row);
    }

    private void ApplyFilter()
    {
        _listView.BeginUpdate();
        _listView.Items.Clear();
        foreach (var row in _orderedRows)
        {
            if (row.Tag is DownloadItem item && PassesFilter(item))
                _listView.Items.Add(row);
        }
        _listView.EndUpdate();
    }

    private void EnsureAnimating()
    {
        if (!_animationTimer.Enabled) _animationTimer.Start();
    }

    private void OnAnimationTick()
    {
        var moved = _progress.Advance();
        if (moved.Count == 0)
        {
            _animationTimer.Stop();
            return;
        }

        foreach (var id in moved)
        {
            if (_rowsById.TryGetValue(id, out var row) && row.ListView is not null)
                _listView.Invalidate(row.Bounds);
        }
    }

    private void UpdateStatusBar()
    {
        int total = _orderedRows.Count;
        var items = _orderedRows.Select(r => (DownloadItem)r.Tag!).ToList();
        int active = items.Count(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Merging);
        int completed = items.Count(i => i.Status == DownloadStatus.Completed);
        double totalSpeed = items
            .Where(i => i.Status is DownloadStatus.Downloading)
            .Sum(i => i.CurrentSpeedBytesPerSecond);

        string status = $"{total} download{(total == 1 ? "" : "s")}   ·   {active} active   ·   {completed} complete";
        if (_statusLabel.Text != status) _statusLabel.Text = status;

        string speed = active > 0 ? $"▼ {ByteFormatter.FormatSpeed(totalSpeed)}" : string.Empty;
        if (_speedLabel.Text != speed) _speedLabel.Text = speed;
    }

    // ------------------------------------------------------------ Actions --

    private DownloadItem? SelectedItem =>
        _listView.SelectedItems.Count > 0 ? (DownloadItem)_listView.SelectedItems[0].Tag! : null;

    private void OnRowActivated()
    {
        var item = SelectedItem;
        if (item is null) return;

        if (item.Status == DownloadStatus.Completed && File.Exists(item.FullPath))
        {
            OpenSelectedFile();
            return;
        }
        OpenSelectedDetailsWindow();
    }

    private void OpenSelectedDetailsWindow()
    {
        var item = SelectedItem;
        if (item is not null) OpenDetailsWindow(item);
    }

    private void OpenDetailsWindow(DownloadItem item)
    {
        if (_openDetailForms.TryGetValue(item.Id, out var existing))
        {
            if (existing.WindowState == FormWindowState.Minimized)
                existing.WindowState = FormWindowState.Normal;
            existing.BringToFront();
            existing.Focus();
            return;
        }

        var detailsForm = new DownloadDetailsForm(item, _downloadManager);
        detailsForm.FormClosed += (_, _) => _openDetailForms.Remove(item.Id);
        _openDetailForms[item.Id] = detailsForm;
        detailsForm.Show(this);
    }

    /// <summary>
    /// Called when a second QuickByte launch hands its command line over (see
    /// <see cref="SingleInstance"/>): surface this window and, if the launch
    /// carried a link, open the Add dialog on it. Already marshaled onto the UI
    /// thread by <see cref="Program"/>.
    /// </summary>
    public void HandleSecondInstance(string payload)
    {
        RestoreAndActivate();

        string? url = SingleInstance.FindUrl(new[] { payload });
        if (url is not null) OnAddDownloadClicked(url);
    }

    private void RestoreAndActivate()
    {
        if (WindowState == FormWindowState.Minimized)
            WindowState = FormWindowState.Normal;

        Show();
        Activate();

        // A process that isn't already in the foreground can't simply take focus
        // — Windows flashes the taskbar button instead. Asserting TopMost for an
        // instant pulls the window up without a P/Invoke to
        // AllowSetForegroundWindow, which is the only other way through.
        bool wasTopMost = TopMost;
        TopMost = true;
        TopMost = wasTopMost;
        BringToFront();
    }

    private async void OnAddDownloadClicked(string? initialUrl = null)
    {
        using var dialog = new NewDownloadForm(_settingsService, initialUrl);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;

        var result = dialog.Result;
        await _downloadManager.AddDownloadAsync(
            result.Url, result.FileInfo, result.SaveFolder, result.FileName, result.ConnectionsCount);
    }

    private async Task OnResumeClickedAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        await _downloadManager.ResumeAsync(item.Id);
    }

    private void OnPauseClicked()
    {
        var item = SelectedItem;
        if (item is null) return;
        _downloadManager.Pause(item.Id);
    }

    private void OnStopClicked()
    {
        var item = SelectedItem;
        if (item is null) return;
        _downloadManager.Stop(item.Id);
    }

    private void OnStopAllClicked()
    {
        foreach (var item in _orderedRows.Select(r => (DownloadItem)r.Tag!).ToList())
        {
            if (item.Status is DownloadStatus.Downloading or DownloadStatus.Connecting)
                _downloadManager.Pause(item.Id);
        }
    }

    private async Task OnRetryClickedAsync()
    {
        var item = SelectedItem;
        if (item is null) return;
        await _downloadManager.RetryAsync(item.Id);
    }

    private void OnRemoveClicked()
    {
        var item = SelectedItem;
        if (item is null) return;

        var confirm = MessageBox.Show(this, $"Remove \"{item.FileName}\" from the list?\nAlso delete the downloaded file?",
            "Remove Download", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (confirm == DialogResult.Cancel) return;

        _downloadManager.Remove(item.Id, deleteFile: confirm == DialogResult.Yes);
    }

    private void OnDeleteCompletedClicked()
    {
        var completed = _orderedRows.Select(r => (DownloadItem)r.Tag!).Where(i => i.Status == DownloadStatus.Completed).ToList();
        if (completed.Count == 0) return;

        var confirm = MessageBox.Show(this, $"Remove {completed.Count} completed download(s) from the list?\n(Downloaded files are kept.)",
            "Delete Completed", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (confirm != DialogResult.Yes) return;

        foreach (var item in completed)
            _downloadManager.Remove(item.Id, deleteFile: false);
    }

    private void OpenSelectedFile()
    {
        var item = SelectedItem;
        if (item is null) return;
        FileLauncher.OpenFile(item.FullPath);
    }

    private void OnOpenFolderClicked()
    {
        var item = SelectedItem;
        if (item is null) return;
        FileLauncher.RevealInExplorer(item.FullPath, item.SaveFolder);
    }

    private void OnSettingsClicked()
    {
        using var settingsForm = new SettingsForm(_settingsService);
        settingsForm.ShowDialog(this);
    }

    private void UpdateActionButtonsState()
    {
        var item = SelectedItem;
        bool hasSelection = item is not null;
        var items = _orderedRows.Select(r => (DownloadItem)r.Tag!).ToList();

        _resumeButton.Enabled = hasSelection && item!.Status is DownloadStatus.Paused or DownloadStatus.Queued or DownloadStatus.Failed or DownloadStatus.Cancelled;
        _pauseButton.Enabled = hasSelection && item!.Status is DownloadStatus.Downloading or DownloadStatus.Connecting;
        _stopAllButton.Enabled = items.Any(i => i.Status is DownloadStatus.Downloading or DownloadStatus.Connecting);
        _deleteButton.Enabled = hasSelection;
        _propertiesButton.Enabled = hasSelection;
        _deleteCompletedButton.Enabled = items.Any(i => i.Status == DownloadStatus.Completed);
    }
}
