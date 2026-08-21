using QuickByte.Core.Enums;

namespace QuickByte.Core.Events;

/// <summary>
/// Raised whenever a download transitions between lifecycle states
/// (Queued -> Connecting -> Downloading -> Completed, etc).
/// </summary>
public sealed class DownloadStatusChangedEventArgs : EventArgs
{
    public required Guid DownloadId { get; init; }
    public required DownloadStatus OldStatus { get; init; }
    public required DownloadStatus NewStatus { get; init; }
    public string? ErrorMessage { get; init; }
}
