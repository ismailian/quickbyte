namespace QuickByte.Core.Events;

/// <summary>
/// Aggregated progress for a whole download (sum of all its connections),
/// raised on a throttled timer so the UI never gets flooded with updates.
/// </summary>
public sealed class DownloadProgressEventArgs : EventArgs
{
    public required Guid DownloadId { get; init; }
    public required long DownloadedBytes { get; init; }
    public required long TotalBytes { get; init; }
    public required double SpeedBytesPerSecond { get; init; }
    public required TimeSpan? EstimatedTimeRemaining { get; init; }

    /// <summary>
    /// Set only while the chunks are being concatenated (0-100). Merge progress
    /// is reported on its own channel rather than through
    /// <see cref="DownloadedBytes"/> so the overall progress bar can stay pinned
    /// at 100% instead of walking backwards once every byte is already on disk.
    /// </summary>
    public double? MergePercentage { get; init; }
}
