using System.Threading;
using QuickByte.Core.Events;
using QuickByte.Core.Models;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// Supervises the whole set of connections for one download: splits the
/// byte range, assigns each connection its slice and temp chunk path,
/// tracks aggregate progress, and signals when every connection has
/// finished so the file merger can take over.
/// </summary>
public interface IConnectionPoolManager
{
    event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    event EventHandler<ConnectionsSnapshotEventArgs>? ConnectionsChanged;
    event EventHandler<string>? ConnectionFailed;

    IReadOnlyList<ConnectionInfo> Snapshot { get; }

    /// <summary>
    /// Starts (or resumes) all connections for the given item/file info and
    /// completes once every connection has finished, failed permanently, or
    /// the token is cancelled (pause/stop).
    /// </summary>
    Task<bool> RunAsync(DownloadItem item, RemoteFileInfo fileInfo, DownloadSettings settings, CancellationToken cancellationToken);

    IReadOnlyList<string> GetOrderedChunkPaths();
}
