using QuickByte.Core.Enums;
using QuickByte.Core.Models;

namespace QuickByte.Core.Helpers;

/// <summary>
/// Which lifecycle commands are legal for a download in its current state.
///
/// This lives in Core rather than in a form because it is a statement about
/// <see cref="DownloadStatus"/>, not about chrome: the same predicates decide
/// whether a toolbar button is greyed, whether a context-menu entry is shown at
/// all, and whether a tray command has anything to act on. Keeping one copy is
/// what stops the three from drifting — the menus used to offer Resume on a
/// download that was already running.
/// </summary>
public static class DownloadActions
{
    /// <summary>Start or continue: anything not already in flight or finished.</summary>
    public static bool CanResume(DownloadItem item) =>
        item.Status is DownloadStatus.Queued or DownloadStatus.Paused
            or DownloadStatus.Failed or DownloadStatus.Cancelled;

    /// <summary>
    /// Only a transfer actually in flight can be paused. <see cref="DownloadStatus.Merging"/>
    /// is deliberately excluded — every byte is already on disk by then and the
    /// merge has no resumable mid-point.
    /// </summary>
    public static bool CanPause(DownloadItem item) =>
        item.Status is DownloadStatus.Connecting or DownloadStatus.Downloading;

    /// <summary>
    /// Stop discards the partial data (it deletes the temp folder), so it is
    /// offered for anything unfinished — including a paused download, where it
    /// is the "discard" half of the pause/discard distinction.
    /// </summary>
    public static bool CanStop(DownloadItem item) =>
        item.Status is DownloadStatus.Queued or DownloadStatus.Connecting
            or DownloadStatus.Downloading or DownloadStatus.Paused;

    /// <summary>Retry re-resolves the remote file and starts over; only useful once a download has ended badly.</summary>
    public static bool CanRetry(DownloadItem item) =>
        item.Status is DownloadStatus.Failed or DownloadStatus.Cancelled;

    public static bool CanOpenFile(DownloadItem item) =>
        item.Status == DownloadStatus.Completed && File.Exists(item.FullPath);

    public static bool CanOpenFolder(DownloadItem item) =>
        !string.IsNullOrWhiteSpace(item.SaveFolder) && Directory.Exists(item.SaveFolder);

    /// <summary>Occupying a connection slot right now — drives the status bar, the tray tooltip and "Stop All".</summary>
    public static bool IsActive(DownloadItem item) =>
        item.Status is DownloadStatus.Connecting or DownloadStatus.Downloading or DownloadStatus.Merging;
}
