using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;

namespace QuickByte.UI.Controls;

/// <summary>
/// Owns the notification-area icon and its menu. QuickByte keeps running with
/// its window closed — a download manager that dies when you tidy your desktop
/// is not much of a download manager — so the tray icon is the app's only
/// remaining presence in that state, and its Exit entry is the one command that
/// really shuts the process down.
///
/// The controller raises intent as events rather than acting on the manager
/// itself: every command already has exactly one implementation on
/// <see cref="Forms.MainForm"/> (confirmation prompts and all), and a second
/// copy here is how the two would drift. The single exception is menu
/// <em>state</em>, which is computed straight from
/// <see cref="DownloadActions"/> against the live download list, for the same
/// reason the window's menus are: an entry that cannot do anything should not
/// invite a click.
/// </summary>
public sealed class TrayIconController : IDisposable
{
    /// <summary>Win32 caps NOTIFYICONDATA's tip at 64 characters including the terminator.</summary>
    private const int MaxTooltipLength = 63;

    private readonly IDownloadManager _downloadManager;
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _openItem;
    private readonly ToolStripMenuItem _addUrlItem;
    private readonly ToolStripMenuItem _pauseAllItem;
    private readonly ToolStripMenuItem _resumeAllItem;
    private readonly ToolStripMenuItem _exitItem;

    private bool _hintShown;

    /// <summary>The user asked for the main window back (tray double-click or "Open QuickByte").</summary>
    public event EventHandler? OpenRequested;
    public event EventHandler? AddUrlRequested;
    public event EventHandler? PauseAllRequested;
    public event EventHandler? ResumeAllRequested;

    /// <summary>The user chose Exit — the only path that closes the application for real.</summary>
    public event EventHandler? ExitRequested;

    public TrayIconController(IDownloadManager downloadManager)
    {
        _downloadManager = downloadManager;

        _openItem = MenuItem("&Open QuickByte", BrandIcon.CreateBitmap(16), (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        _openItem.Font = Theme.UiBold; // the default action, and the one a double-click performs
        _addUrlItem = MenuItem("Add &URL...", IconFactory.AddUrl(16), (_, _) => AddUrlRequested?.Invoke(this, EventArgs.Empty));
        _pauseAllItem = MenuItem("&Pause All", IconFactory.Pause(16), (_, _) => PauseAllRequested?.Invoke(this, EventArgs.Empty));
        _resumeAllItem = MenuItem("&Resume All", IconFactory.Resume(16), (_, _) => ResumeAllRequested?.Invoke(this, EventArgs.Empty));
        _exitItem = MenuItem("E&xit", IconFactory.Exit(16), (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

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
            _openItem, _addUrlItem, new ToolStripSeparator(),
            _pauseAllItem, _resumeAllItem, new ToolStripSeparator(),
            _exitItem
        });
        menu.Opening += (_, _) => UpdateMenuState();

        _notifyIcon = new NotifyIcon
        {
            Icon = BrandIcon.App,
            Text = "QuickByte",
            ContextMenuStrip = menu,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    private static ToolStripMenuItem MenuItem(string text, Image icon, EventHandler onClick)
    {
        var item = new ToolStripMenuItem(text, icon);
        item.Click += onClick;
        return item;
    }

    private void UpdateMenuState()
    {
        var items = _downloadManager.Downloads;
        _pauseAllItem.Enabled = items.Any(DownloadActions.CanPause);
        _resumeAllItem.Enabled = items.Any(DownloadActions.CanResume);
    }

    /// <summary>
    /// Rewrites the hover tooltip so the tray icon reports what the app is doing
    /// while its window is closed. Truncated defensively: NotifyIcon throws on an
    /// over-long tip rather than trimming it.
    /// </summary>
    public void UpdateTooltip(int activeCount, double totalSpeedBytesPerSecond)
    {
        string text = activeCount == 0
            ? "QuickByte — idle"
            : $"QuickByte — {activeCount} active · {ByteFormatter.FormatSpeed(totalSpeedBytesPerSecond)}";

        if (text.Length > MaxTooltipLength) text = text[..MaxTooltipLength];
        if (_notifyIcon.Text != text) _notifyIcon.Text = text;
    }

    /// <summary>
    /// Called when the window has just gone away instead of the app. Explains
    /// itself once per run — the first time a close button doesn't close
    /// something is the only time it is surprising.
    /// </summary>
    public void NotifyWindowHidden()
    {
        if (_hintShown) return;
        _hintShown = true;
        _notifyIcon.ShowBalloonTip(4000, "QuickByte is still running",
            "Downloads continue in the background. Right-click this icon and choose Exit to quit.",
            ToolTipIcon.Info);
    }

    public void Dispose()
    {
        // Hide before disposing: an icon whose owner process is gone lingers in
        // the notification area until something makes Windows repaint it.
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }
}
