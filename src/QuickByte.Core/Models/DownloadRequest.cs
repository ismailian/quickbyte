namespace QuickByte.Core.Models;

/// <summary>
/// Everything <see cref="Interfaces.IDownloadManager.AddDownloadAsync"/> needs
/// to create a download: the resolved metadata, where it lands, and how it
/// should identify itself to the server.
///
/// A record rather than a widening parameter list — the call already carried
/// five arguments before credentials and headers joined them, and the two new
/// ones are optional for most downloads, which is exactly the shape positional
/// parameters handle worst.
/// </summary>
public sealed record DownloadRequest(
    string Url,
    RemoteFileInfo FileInfo,
    string SaveFolder,
    string FileName,
    int ConnectionsCount)
{
    public DownloadCredentials? Credentials { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Whether the download starts the moment it is added. False for one added
    /// to a queue, which is what makes it sit at <see cref="Enums.DownloadStatus.Queued"/>
    /// until the queue reaches it. Note that
    /// <see cref="Interfaces.IDownloadManager.AddDownloadAsync"/> only returns
    /// once the download has finished when this is true — a caller that needs
    /// the item back promptly is a caller that is queueing it.
    /// </summary>
    public bool StartImmediately { get; init; } = true;
}
