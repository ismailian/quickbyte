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
    private readonly IUpdateService _updateService;
    private readonly IRemoteFileInfoProvider _fileInfoProvider;
    private readonly IBrowserIntegrationService _browserIntegration;

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

    // Menu-bar entries that mirror the toolbar commands. Held so one
    // UpdateCommandStates() pass can grey them in step with the buttons instead
    // of leaving the menus permanently enabled.
    private ToolStripMenuItem _menuOpenFile = null!;
    private ToolStripMenuItem _menuOpenFolder = null!;
    private ToolStripMenuItem _menuResume = null!;
    private ToolStripMenuItem _menuPause = null!;
    private ToolStripMenuItem _menuStopAll = null!;
    private ToolStripMenuItem _menuDelete = null!;
    private ToolStripMenuItem _menuDeleteCompleted = null!;
    private ToolStripMenuItem _menuProperties = null!;
    private ToolStripMenuItem _menuRetry = null!;

    /// <summary>
    /// Held so the manual check can grey itself while it is in flight — the
    /// request is short but not instant, and a second one would open a second
    /// update window.
    /// </summary>
    private ToolStripMenuItem _menuCheckUpdates = null!;

    // Row context-menu entries. Unlike the toolbar these are *hidden* rather
    // than greyed when they don't apply — a right-click menu is read as a list
    // of what you can do here, so an inert Resume on a running download is noise.
    private ToolStripMenuItem _rowOpenFile = null!;
    private ToolStripMenuItem _rowOpenFolder = null!;
    private ToolStripMenuItem _rowResume = null!;
    private ToolStripMenuItem _rowPause = null!;
    private ToolStripMenuItem _rowStop = null!;
    private ToolStripMenuItem _rowRetry = null!;
    private ToolStripMenuItem _rowRemove = null!;
    private ToolStripMenuItem _rowProperties = null!;

    private TrayIconController _trayIcon = null!;

    /// <summary>Windows taken down by <see cref="HideToTray"/>, to be put back when the window returns.</summary>
    private readonly List<Form> _hiddenOwnedForms = new();

    /// <summary>
    /// Set only by <see cref="ExitApplication"/>. Until then the close button is
    /// a hide button — see <see cref="OnFormClosing"/>.
    /// </summary>
    private bool _exiting;

    /// <summary>Startup update check state — see <see cref="OnShown"/> and <see cref="ShowUpdateWindow"/>.</summary>
    private bool _startupUpdateCheckDone;
    private bool _updateWindowOpen;

    public MainForm(
        IDownloadManager downloadManager,
        ISettingsService settingsService,
        IUpdateService updateService,
        IRemoteFileInfoProvider fileInfoProvider,
        IBrowserIntegrationService browserIntegration)
    {
        _downloadManager = downloadManager;
        _settingsService = settingsService;
        _updateService = updateService;
        _fileInfoProvider = fileInfoProvider;
        _browserIntegration = browserIntegration;

        BuildUi();
        BuildTrayIcon();
        WireManagerEvents();
        PopulateFromExistingDownloads();
        UpdateCommandStates();

        _animationTimer.Tick += (_, _) => OnAnimationTick();
        FormClosed += (_, _) =>
        {
            _animationTimer.Dispose();
            _trayIcon.Dispose();
        };
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

        _menuResume = MenuEntry("&Resume", IconFactory.Resume(16), async (_, _) => await OnResumeClickedAsync());
        _menuPause = MenuEntry("&Pause", IconFactory.Pause(16), (_, _) => OnPauseClicked());
        _menuStopAll = MenuEntry("Stop &All", IconFactory.StopAll(16), (_, _) => OnStopAllClicked());
        _menuDelete = MenuEntry("&Delete", IconFactory.Delete(16), (_, _) => OnRemoveClicked());
        _menuDeleteCompleted = MenuEntry("Delete &Completed", IconFactory.DeleteCompleted(16), (_, _) => OnDeleteCompletedClicked());

        var tasksMenu = new ToolStripMenuItem("&Tasks");
        tasksMenu.DropDownItems.Add("Add &URL...", IconFactory.AddUrl(16), (_, _) => OnAddDownloadClicked());
        tasksMenu.DropDownItems.Add(new ToolStripSeparator());
        tasksMenu.DropDownItems.Add(_menuResume);
        tasksMenu.DropDownItems.Add(_menuPause);
        tasksMenu.DropDownItems.Add(_menuStopAll);
        tasksMenu.DropDownItems.Add(new ToolStripSeparator());
        tasksMenu.DropDownItems.Add(_menuDelete);
        tasksMenu.DropDownItems.Add(_menuDeleteCompleted);
        tasksMenu.DropDownItems.Add(new ToolStripSeparator());
        tasksMenu.DropDownItems.Add("&Options...", IconFactory.Settings(16), (_, _) => OnSettingsClicked());

        _menuOpenFile = MenuEntry("&Open File", IconFactory.OpenFile(16), (_, _) => OpenSelectedFile());
        _menuOpenFolder = MenuEntry("Open Containing &Folder", IconFactory.OpenFolder(16), (_, _) => OnOpenFolderClicked());

        var fileMenu = new ToolStripMenuItem("&File");
        fileMenu.DropDownItems.Add(_menuOpenFile);
        fileMenu.DropDownItems.Add(_menuOpenFolder);
        fileMenu.DropDownItems.Add(new ToolStripSeparator());
        fileMenu.DropDownItems.Add("Close to &Tray", null, (_, _) => HideToTray());
        fileMenu.DropDownItems.Add("E&xit", IconFactory.Exit(16), (_, _) => ExitApplication());

        _menuProperties = MenuEntry("&Properties...", IconFactory.Properties(16), (_, _) => OpenSelectedDetailsWindow());
        _menuRetry = MenuEntry("&Retry", IconFactory.Retry(16), async (_, _) => await OnRetryClickedAsync());

        var downloadsMenu = new ToolStripMenuItem("&Downloads");
        downloadsMenu.DropDownItems.Add(_menuProperties);
        downloadsMenu.DropDownItems.Add(_menuRetry);

        var viewMenu = new ToolStripMenuItem("&View");
        var toolbarToggle = new ToolStripMenuItem("&Toolbar") { CheckOnClick = true, Checked = true };
        toolbarToggle.Click += (_, _) => _toolStrip.Visible = toolbarToggle.Checked;
        var sidebarToggle = new ToolStripMenuItem("&Categories Panel") { CheckOnClick = true, Checked = true };
        sidebarToggle.Click += (_, _) => SetSidebarVisible(sidebarToggle.Checked);
        viewMenu.DropDownItems.Add(toolbarToggle);
        viewMenu.DropDownItems.Add(sidebarToggle);

        _menuCheckUpdates = MenuEntry("Check for &Updates...", IconFactory.Update(16), async (_, _) => await CheckForUpdatesAsync());

        var helpMenu = new ToolStripMenuItem("&Help");
        helpMenu.DropDownItems.Add(_menuCheckUpdates);
        helpMenu.DropDownItems.Add(new ToolStripSeparator());
        helpMenu.DropDownItems.Add("&About QuickByte...", IconFactory.Properties(16), (_, _) => ShowAboutDialog());

        // The selection can change without the mouse ever touching a row (a
        // status change re-filters the list), so the states are refreshed as the
        // drop-down opens rather than trusted to be current.
        foreach (var top in new[] { fileMenu, tasksMenu, downloadsMenu })
            top.DropDownOpening += (_, _) => UpdateCommandStates();

        menu.Items.AddRange(new ToolStripItem[] { fileMenu, tasksMenu, downloadsMenu, viewMenu, helpMenu });
        return menu;
    }

    private static ToolStripMenuItem MenuEntry(string text, Bitmap icon, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text, icon);
        item.Click += onClick;
        return item;
    }

    private void ShowAboutDialog()
    {
        using var about = new AboutForm();
        about.ShowDialog(this);
    }

    // ----------------------------------------------------------- Updates --

    /// <summary>
    /// Fires the background update check once, as the window first appears —
    /// not from the constructor, because a prompt needs a window to be modal
    /// over. <see cref="_startupUpdateCheckDone"/> guards it rather than trusting
    /// OnShown to be a once-per-process event: this form is shown again every
    /// time it comes back from the tray.
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_startupUpdateCheckDone) return;

        _startupUpdateCheckDone = true;
        _ = RunStartupUpdateCheckAsync();
    }

    /// <summary>
    /// The unattended check. It only ever <em>offers</em> — nothing is
    /// downloaded until the user says so in <see cref="UpdateForm"/>, and a
    /// failure is swallowed, because nobody asked for this and an error dialog
    /// on a launch the user started for some other reason is noise.
    /// </summary>
    private async Task RunStartupUpdateCheckAsync()
    {
        UpdateCheckResult result;
        try
        {
            result = await _updateService.CheckForUpdateAsync(AppVersion.Display);
        }
        catch
        {
            return;
        }

        if (IsDisposed || !result.UpdateAvailable || result.Manifest is null) return;
        ShowUpdateWindow(result.Manifest, UpdatePromptMode.Prompt);
    }

    /// <summary>
    /// Help &gt; Check for Updates. The user asked, so this one reports back
    /// either way — and when there is an update it goes straight to downloading
    /// and running the installer rather than asking a question they just
    /// answered by opening the menu.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        _menuCheckUpdates.Enabled = false;
        UseWaitCursor = true;

        UpdateCheckResult? result = null;
        string? error = null;
        try
        {
            result = await _updateService.CheckForUpdateAsync(AppVersion.Display);
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            // Dropped before anything modal opens: a message box under an
            // hourglass reads as a window that is still working.
            if (!IsDisposed)
            {
                UseWaitCursor = false;
                _menuCheckUpdates.Enabled = true;
            }
        }

        if (IsDisposed) return;

        if (error is not null)
        {
            MessageBox.Show(this, $"Could not check for updates.\n\n{error}",
                "Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (result!.UpdateAvailable && result.Manifest is not null)
        {
            ShowUpdateWindow(result.Manifest, UpdatePromptMode.Automatic);
            return;
        }

        MessageBox.Show(this, $"QuickByte {AppVersion.Display} is the latest version.",
            "Check for Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Opens the update window, and closes QuickByte if it got as far as
    /// starting the installer — setup cannot replace the files this process is
    /// holding open. The flag stops the startup check and a manual one from
    /// stacking two windows describing the same release.
    /// </summary>
    private void ShowUpdateWindow(UpdateManifest manifest, UpdatePromptMode mode)
    {
        if (_updateWindowOpen) return;
        _updateWindowOpen = true;

        DialogResult outcome;
        try
        {
            using var window = new UpdateForm(_updateService, manifest, AppVersion.Display, mode);
            outcome = window.ShowDialog(this);
        }
        finally
        {
            _updateWindowOpen = false;
        }

        if (outcome == DialogResult.OK) ExitApplication();
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
        listView.Columns.Add("Size", 100);
        listView.Columns.Add("Progress", 118);
        listView.Columns.Add("Transfer Rate", 116);
        listView.Columns.Add("Time Left", 96);
        listView.Columns.Add("Status", 140);
        listView.Columns.Add("Description", 140);

        listView.DrawSubItem += ListView_DrawSubItem;
        listView.MouseDoubleClick += (_, _) => OnRowActivated();
        listView.MouseDown += ListView_MouseDown;
        listView.SelectedIndexChanged += (_, _) => UpdateCommandStates();
        listView.KeyDown += ListView_KeyDown;
        listView.ContextMenuStrip = BuildRowContextMenu();

        return listView;
    }

    /// <summary>
    /// A ListView doesn't move the selection on a right-click, so the menu would
    /// otherwise describe whichever row happened to be selected last rather than
    /// the one under the cursor. Right-clicking empty space clears the selection,
    /// which is what makes the menu decline to open there.
    /// </summary>
    private void ListView_MouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;

        var hit = _listView.GetItemAt(e.X, e.Y);
        if (hit is null)
        {
            _listView.SelectedItems.Clear();
            return;
        }

        // Leave an existing multi-selection intact if the click landed inside it.
        if (hit.Selected) return;

        _listView.SelectedItems.Clear();
        hit.Selected = true;
        hit.Focused = true;
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
        _rowOpenFile = MenuEntry("Open File", IconFactory.OpenFile(16), (_, _) => OpenSelectedFile());
        _rowOpenFolder = MenuEntry("Open Containing Folder", IconFactory.OpenFolder(16), (_, _) => OnOpenFolderClicked());
        _rowResume = MenuEntry("Resume", IconFactory.Resume(16), async (_, _) => await OnResumeClickedAsync());
        _rowPause = MenuEntry("Pause", IconFactory.Pause(16), (_, _) => OnPauseClicked());
        _rowStop = MenuEntry("Stop", IconFactory.Stop(16), (_, _) => OnStopClicked());
        _rowRetry = MenuEntry("Retry", IconFactory.Retry(16), async (_, _) => await OnRetryClickedAsync());
        _rowRemove = MenuEntry("Remove", IconFactory.Delete(16), (_, _) => OnRemoveClicked());
        _rowProperties = MenuEntry("Properties...", IconFactory.Properties(16), (_, _) => OpenSelectedDetailsWindow());

        var menu = new ContextMenuStrip
        {
            RenderMode = ToolStripRenderMode.Professional,
            Renderer = new FlatToolStripRenderer(),
            BackColor = Theme.Surface,
            Font = Theme.Ui,
            ShowImageMargin = true
        };
        menu.Items.AddRange(new ToolStripItem[]
        {
            _rowOpenFile, _rowOpenFolder, new ToolStripSeparator(),
            _rowResume, _rowPause, _rowStop, _rowRetry, new ToolStripSeparator(),
            _rowRemove, new ToolStripSeparator(),
            _rowProperties
        });
        menu.Opening += RowContextMenu_Opening;
        return menu;
    }

    /// <summary>
    /// Rebuilds the menu for whatever is selected: every entry that can't act on
    /// the selection is hidden, and the separators left stranded by those
    /// hidden entries go with them. With nothing selected there is no menu at
    /// all rather than a column of dead entries.
    /// </summary>
    private void RowContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        var selection = SelectedItems();
        if (selection.Count == 0) { e.Cancel = true; return; }

        // Open File / Open Folder / Properties act on one download. Rather than
        // silently picking the first of many, they only appear for a single
        // selection; the lifecycle commands below apply to the whole selection
        // and appear whenever any part of it can use them.
        var single = selection.Count == 1 ? selection[0] : null;

        _rowOpenFile.Available = single is not null && DownloadActions.CanOpenFile(single);
        _rowOpenFolder.Available = single is not null && DownloadActions.CanOpenFolder(single);
        _rowProperties.Available = single is not null;

        _rowResume.Available = selection.Any(DownloadActions.CanResume);
        _rowPause.Available = selection.Any(DownloadActions.CanPause);
        _rowStop.Available = selection.Any(DownloadActions.CanStop);
        _rowRetry.Available = selection.Any(DownloadActions.CanRetry);
        _rowRemove.Available = true;

        // Plural labels when the command is about to hit more than one row —
        // "Remove" reading the same for 1 and 12 downloads is how people delete
        // more than they meant to.
        _rowResume.Text = selection.Count > 1 ? $"Resume ({selection.Count(DownloadActions.CanResume)})" : "Resume";
        _rowPause.Text = selection.Count > 1 ? $"Pause ({selection.Count(DownloadActions.CanPause)})" : "Pause";
        _rowStop.Text = selection.Count > 1 ? $"Stop ({selection.Count(DownloadActions.CanStop)})" : "Stop";
        _rowRetry.Text = selection.Count > 1 ? $"Retry ({selection.Count(DownloadActions.CanRetry)})" : "Retry";
        _rowRemove.Text = selection.Count > 1 ? $"Remove {selection.Count} Downloads" : "Remove";

        TrimSeparators(((ContextMenuStrip)sender!).Items);
        UpdateCommandStates();
    }

    /// <summary>
    /// Hides leading, trailing and doubled separators left behind by hidden
    /// entries. Reads and writes <see cref="ToolStripItem.Available"/>, not
    /// <c>Visible</c>: the latter also reports whether the parent drop-down is on
    /// screen, so during <c>Opening</c> it is false for every item in the menu.
    /// </summary>
    private static void TrimSeparators(ToolStripItemCollection items)
    {
        ToolStripSeparator? pending = null;
        bool anyBefore = false;

        foreach (ToolStripItem entry in items)
        {
            if (entry is ToolStripSeparator separator)
            {
                separator.Available = false;
                if (anyBefore) pending = separator;
                continue;
            }

            if (!entry.Available) continue;
            if (pending is not null) { pending.Available = true; pending = null; }
            anyBefore = true;
        }
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
        DownloadStatus.Merging => $"Merging {ByteFormatter.FormatPercentage(item.MergeProgressPercentage)}",
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
        UpdateCommandStates();
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
        _listView.InvalidateRow(row);

        SetRowVisible(row, PassesFilter(item));
        UpdateCommandStates();
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

        // Only the progress cell moved, so only the progress cell is repainted.
        // Invalidating the whole row here re-ran every column's owner-draw 60
        // times a second — the category icon blit and six text runs — on top of
        // whatever row the pointer was hovering, which is what made a hovered
        // row look like it was flickering.
        foreach (var id in moved)
        {
            if (_rowsById.TryGetValue(id, out var row))
                _listView.InvalidateCell(row, ProgressColumnIndex);
        }
    }

    private void UpdateStatusBar()
    {
        int total = _orderedRows.Count;
        var items = _orderedRows.Select(r => (DownloadItem)r.Tag!).ToList();
        int active = items.Count(DownloadActions.IsActive);
        int completed = items.Count(i => i.Status == DownloadStatus.Completed);
        double totalSpeed = items
            .Where(i => i.Status is DownloadStatus.Downloading)
            .Sum(i => i.CurrentSpeedBytesPerSecond);

        string status = $"{total} download{(total == 1 ? "" : "s")}   ·   {active} active   ·   {completed} complete";
        if (_statusLabel.Text != status) _statusLabel.Text = status;

        string speed = active > 0 ? $"▼ {ByteFormatter.FormatSpeed(totalSpeed)}" : string.Empty;
        if (_speedLabel.Text != speed) _speedLabel.Text = speed;

        // Same numbers on the tray tooltip: with the window hidden it is the only
        // place they are readable.
        _trayIcon.UpdateTooltip(active, totalSpeed);
    }

    // ------------------------------------------------------------ Actions --

    private DownloadItem? SelectedItem =>
        _listView.SelectedItems.Count > 0 ? (DownloadItem)_listView.SelectedItems[0].Tag! : null;

    /// <summary>
    /// Snapshot of the selection. A list, not the live collection: the lifecycle
    /// commands change status, which re-filters rows out from under an
    /// enumeration of <see cref="ListView.SelectedItems"/>.
    /// </summary>
    private List<DownloadItem> SelectedItems() =>
        _listView.SelectedItems.Cast<ListViewItem>().Select(row => (DownloadItem)row.Tag!).ToList();

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
        RestoreHiddenOwnedForms();
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

    /// <summary>
    /// Called when the browser extension hands over a link it intercepted.
    /// Already marshaled onto the UI thread by <see cref="Program"/>, exactly as
    /// a second launch is — the bridge raises its event on a thread-pool thread.
    /// </summary>
    public void HandleCapturedDownload(CapturedDownload captured)
    {
        // Surfaced first: the click that produced this happened in the browser,
        // so the dialog would otherwise open behind whatever the user is looking
        // at and the download would sit unstarted.
        RestoreAndActivate();
        OnAddDownloadClicked(captured: captured);
    }

    private async void OnAddDownloadClicked(string? initialUrl = null, CapturedDownload? captured = null)
    {
        using var dialog = new NewDownloadForm(_settingsService, _fileInfoProvider, initialUrl, captured);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Result is null) return;

        await _downloadManager.AddDownloadAsync(dialog.Result);
    }

    /// <summary>
    /// Resumes every selected download that can be resumed. The tasks are
    /// started first and awaited together, never awaited one at a time:
    /// <see cref="IDownloadManager.ResumeAsync"/> completes when the *download*
    /// does, so a sequential loop would hold each one back until the previous
    /// file had finished. Overlap is the concurrency gate's business, not this
    /// handler's.
    /// </summary>
    private async Task OnResumeClickedAsync()
    {
        var tasks = SelectedItems()
            .Where(DownloadActions.CanResume)
            .Select(item => _downloadManager.ResumeAsync(item.Id))
            .ToList();
        if (tasks.Count == 0) return;
        await Task.WhenAll(tasks);
    }

    private void OnPauseClicked()
    {
        foreach (var item in SelectedItems().Where(DownloadActions.CanPause))
            _downloadManager.Pause(item.Id);
    }

    private void OnStopClicked()
    {
        var stoppable = SelectedItems().Where(DownloadActions.CanStop).ToList();
        if (stoppable.Count == 0) return;

        // Stop deletes the chunk files, so unlike Pause it can't be undone by
        // clicking Resume. A single row stays a single click, as it always was;
        // discarding a whole selection's worth of partial data in one go is new
        // and worth a prompt.
        if (stoppable.Count > 1 &&
            MessageBox.Show(this, $"Stop {stoppable.Count} downloads?\nTheir partially downloaded data is discarded.",
                "Stop Downloads", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        foreach (var item in stoppable)
            _downloadManager.Stop(item.Id);
    }

    private void OnStopAllClicked() => _downloadManager.PauseAll();

    private async Task OnRetryClickedAsync()
    {
        var tasks = SelectedItems()
            .Where(DownloadActions.CanRetry)
            .Select(item => _downloadManager.RetryAsync(item.Id))
            .ToList();
        if (tasks.Count == 0) return;
        await Task.WhenAll(tasks);
    }

    private void OnRemoveClicked()
    {
        var items = SelectedItems();
        if (items.Count == 0) return;

        string prompt = items.Count == 1
            ? $"Remove \"{items[0].FileName}\" from the list?\nAlso delete the downloaded file?"
            : $"Remove {items.Count} downloads from the list?\nAlso delete the downloaded files?";

        var confirm = MessageBox.Show(this, prompt, "Remove Download", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (confirm == DialogResult.Cancel) return;

        foreach (var item in items)
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
        using var settingsForm = new SettingsForm(_settingsService, _browserIntegration);
        settingsForm.ShowDialog(this);
    }

    /// <summary>
    /// Single pass that greys every toolbar button and menu-bar entry against
    /// the current selection, using the same <see cref="DownloadActions"/>
    /// predicates the row context menu hides its entries with. Called on
    /// selection changes, on status changes, and again as each drop-down opens.
    /// </summary>
    private void UpdateCommandStates()
    {
        var selection = SelectedItems();
        var all = _orderedRows.Select(r => (DownloadItem)r.Tag!).ToList();
        var single = selection.Count == 1 ? selection[0] : null;

        bool canResume = selection.Any(DownloadActions.CanResume);
        bool canPause = selection.Any(DownloadActions.CanPause);
        bool canRetry = selection.Any(DownloadActions.CanRetry);
        bool anyRunning = all.Any(DownloadActions.CanPause);
        bool anyCompleted = all.Any(i => i.Status == DownloadStatus.Completed);
        bool hasSelection = selection.Count > 0;

        _resumeButton.Enabled = canResume;
        _pauseButton.Enabled = canPause;
        _stopAllButton.Enabled = anyRunning;
        _deleteButton.Enabled = hasSelection;
        _propertiesButton.Enabled = single is not null;
        _deleteCompletedButton.Enabled = anyCompleted;

        _menuResume.Enabled = canResume;
        _menuPause.Enabled = canPause;
        _menuStopAll.Enabled = anyRunning;
        _menuDelete.Enabled = hasSelection;
        _menuDeleteCompleted.Enabled = anyCompleted;
        _menuRetry.Enabled = canRetry;
        _menuProperties.Enabled = single is not null;
        _menuOpenFile.Enabled = single is not null && DownloadActions.CanOpenFile(single);
        _menuOpenFolder.Enabled = single is not null && DownloadActions.CanOpenFolder(single);
    }

    // ---------------------------------------------------------- Tray + exit --

    private void BuildTrayIcon()
    {
        _trayIcon = new TrayIconController(_downloadManager);
        _trayIcon.OpenRequested += (_, _) => RestoreAndActivate();
        _trayIcon.AddUrlRequested += (_, _) =>
        {
            // The Add dialog is modal on this window, so the window has to be
            // back before it opens — otherwise the dialog is the only thing on
            // screen and dismissing it leaves nothing behind.
            RestoreAndActivate();
            OnAddDownloadClicked();
        };
        _trayIcon.PauseAllRequested += (_, _) => _downloadManager.PauseAll();
        _trayIcon.ResumeAllRequested += (_, _) => ResumeAll();
        _trayIcon.ExitRequested += (_, _) => ExitApplication();
    }

    private async void ResumeAll()
    {
        var tasks = _downloadManager.Downloads
            .Where(DownloadActions.CanResume)
            .Select(item => _downloadManager.ResumeAsync(item.Id))
            .ToList();
        if (tasks.Count == 0) return;
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Puts the whole application out of sight, not just this window. Hiding an
    /// owner does <em>not</em> hide the windows it owns — verified, not assumed —
    /// so any open details or completion window would otherwise be left floating
    /// on the desktop with nothing behind it. They are remembered so the same set
    /// comes back when the window does.
    /// </summary>
    private void HideToTray()
    {
        _hiddenOwnedForms.Clear();
        foreach (var owned in OwnedForms)
        {
            if (!owned.Visible) continue;
            _hiddenOwnedForms.Add(owned);
            owned.Hide();
        }

        Hide();
        _trayIcon.NotifyWindowHidden();
    }

    private void RestoreHiddenOwnedForms()
    {
        foreach (var owned in _hiddenOwnedForms)
        {
            if (!owned.IsDisposed) owned.Show();
        }
        _hiddenOwnedForms.Clear();
    }

    /// <summary>
    /// The only path that really closes QuickByte. Everything else — the title
    /// bar's X, Alt+F4, File &gt; Close to Tray — hides the window and leaves the
    /// downloads running.
    /// </summary>
    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    /// <summary>
    /// Turns a user-initiated close into a hide unless <see cref="ExitApplication"/>
    /// asked for it. On a genuine exit every in-flight download is paused first:
    /// pausing leaves the chunk files on disk and persists the paused status
    /// through the repository, so the next launch resumes from exactly where the
    /// process stopped instead of finding a half-written download that
    /// <c>LoadPersistedDownloads</c> has to downgrade after the fact.
    ///
    /// Windows shutting the session down is not negotiable — that close is
    /// honoured, and the pause still runs.
    /// </summary>
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason is CloseReason.UserClosing or CloseReason.None)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _downloadManager.PauseAll();
        base.OnFormClosing(e);
    }
}
