using QuickByte.Core.Models;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// Factory pattern: builds <see cref="IDownloadConnection"/> instances so the
/// pool manager never depends on the concrete HTTP implementation. Makes it
/// trivial to unit test the pool with fake connections.
/// </summary>
public interface IConnectionFactory
{
    /// <param name="bandwidthLimiter">
    /// Shared by every connection of the download, so the cap applies to the
    /// transfer as a whole rather than to each segment separately. Null means
    /// no throttling.
    /// </param>
    /// <param name="options">
    /// Credentials and extra headers for this download; see
    /// <see cref="RequestOptions"/>. Every connection of a download gets the
    /// same instance.
    /// </param>
    IDownloadConnection Create(
        int connectionId,
        string url,
        long rangeStart,
        long rangeEnd,
        long alreadyDownloaded,
        string chunkFilePath,
        DownloadSettings settings,
        IBandwidthLimiter? bandwidthLimiter = null,
        RequestOptions? options = null);
}
