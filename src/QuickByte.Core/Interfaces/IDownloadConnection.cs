using System.Threading;
using QuickByte.Core.Enums;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// A single, independent worker responsible for downloading exactly one
/// byte range into its own temp chunk file. It knows nothing about sibling
/// connections, the pool, or the UI — it only reports its own state.
/// </summary>
public interface IDownloadConnection
{
    int ConnectionId { get; }
    long RangeStart { get; }
    long RangeEnd { get; }
    long BytesDownloaded { get; }
    int RetryCount { get; }
    ConnectionStatus Status { get; }
    string? LastError { get; }
    string ChunkFilePath { get; }

    /// <summary>Runs the download to completion, retrying transient failures internally.</summary>
    Task RunAsync(CancellationToken cancellationToken);
}
