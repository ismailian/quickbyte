namespace QuickByte.Core.Enums;

/// <summary>
/// Lifecycle states of a download. Drives which sidebar category
/// (In Progress, Queued, Completed, Failed, Paused) an item belongs to.
/// </summary>
public enum DownloadStatus
{
    Queued,
    Connecting,
    Downloading,
    Paused,
    Merging,
    Completed,
    Failed,
    Cancelled
}
