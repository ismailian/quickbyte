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
    Cancelled,

    /// <summary>
    /// The file is assembled and being checked against the checksum the user gave.
    /// Appended rather than placed beside <see cref="Merging"/>: statuses are
    /// persisted as numbers, and moving one would re-read every stored download.
    /// </summary>
    Verifying
}
