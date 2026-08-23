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
}
