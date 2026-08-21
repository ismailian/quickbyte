using QuickByte.Core.Models;

namespace QuickByte.Core.Events;

/// <summary>
/// Raised alongside <see cref="DownloadProgressEventArgs"/>, carrying an
/// immutable snapshot of every connection's state for the Download Details
/// window's connections ListView.
/// </summary>
public sealed class ConnectionsSnapshotEventArgs : EventArgs
{
    public required Guid DownloadId { get; init; }
    public required IReadOnlyList<ConnectionInfo> Connections { get; init; }
}
