using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services.Ftp;

/// <summary>
/// Produces <see cref="FtpDownloadConnection"/> workers.
///
/// Stateless, and with no shared client to hold — unlike
/// <see cref="HttpConnectionFactory"/>, which exists largely to keep one
/// <see cref="System.Net.Http.HttpClient"/> alive across every connection. FTP
/// control channels can't be pooled that way: each carries per-transfer state
/// (transfer type, restart offset, the passive port in flight), so each segment
/// opens and closes its own.
/// </summary>
public sealed class FtpConnectionFactory : IConnectionFactory
{
    public IDownloadConnection Create(
        int connectionId,
        string url,
        long rangeStart,
        long rangeEnd,
        long alreadyDownloaded,
        string chunkFilePath,
        DownloadSettings settings,
        IBandwidthLimiter? bandwidthLimiter = null,
        RequestOptions? options = null)
    {
        return new FtpDownloadConnection(
            connectionId, url, rangeStart, rangeEnd, alreadyDownloaded, chunkFilePath,
            settings, bandwidthLimiter, options);
    }
}
