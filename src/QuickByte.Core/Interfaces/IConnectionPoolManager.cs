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
    /// <exception cref="Exceptions.RemoteFileChangedException">
    /// A connection found the file on the server is no longer the version the
    /// chunks came from. Every connection has been stopped by the time this is
    /// thrown; the chunks are left where they are for the caller to judge.
    /// </exception>
    Task<bool> RunAsync(DownloadItem item, RemoteFileInfo fileInfo, DownloadSettings settings, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every chunk file in <paramref name="tempFolder"/>, so the next run
    /// starts the file from zero. The pool owns this because it owns the chunk
    /// naming; anything else in the folder is left alone.
    /// </summary>
    void DiscardChunks(string tempFolder);

    /// <summary>
    /// Forgets the chunk layout this pool recorded in <paramref name="tempFolder"/>
    /// — called once the chunks have been merged and there is nothing left for it
    /// to describe.
    /// </summary>
    void ForgetLayout(string tempFolder);

    IReadOnlyList<string> GetOrderedChunkPaths();
}
