using QuickByte.Core.Models;

namespace QuickByte.Core.Events;

public enum DownloadListChangeType { Added, Removed }

/// <summary>
/// Raised by <see cref="Services.DownloadManager"/> when a download is added
/// to or removed from the managed collection, so the main window can keep
/// its ListView in sync without re-querying the whole list.
/// </summary>
public sealed class DownloadListChangedEventArgs : EventArgs
{
    public required DownloadListChangeType ChangeType { get; init; }
    public required DownloadItem Item { get; init; }
}
