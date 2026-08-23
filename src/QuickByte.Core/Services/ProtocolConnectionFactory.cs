using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.Core.Services.Ftp;

namespace QuickByte.Core.Services;

/// <summary>
/// Picks the connection factory that matches a URL's scheme — FTP for
/// <c>ftp://</c> and <c>ftps://</c>, HTTP for everything else.
///
/// The dispatch lives here rather than inside
/// <see cref="ConnectionPoolManager"/> so the pool keeps knowing nothing about
/// protocols: it splits a range and runs N workers, and whether those workers
/// speak HTTP or FTP is settled before it ever sees them. Adding a third
/// protocol means adding a factory and one line here, not touching the pool, the
/// service, or the manager.
/// </summary>
public sealed class ProtocolConnectionFactory : IConnectionFactory
{
    private readonly IConnectionFactory _http;
    private readonly IConnectionFactory _ftp;

    public ProtocolConnectionFactory(IConnectionFactory? http = null, IConnectionFactory? ftp = null)
    {
        _http = http ?? new HttpConnectionFactory();
        _ftp = ftp ?? new FtpConnectionFactory();
    }

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
        var factory = FtpUrl.IsFtp(url) ? _ftp : _http;
        return factory.Create(
            connectionId, url, rangeStart, rangeEnd, alreadyDownloaded, chunkFilePath,
            settings, bandwidthLimiter, options);
    }
}
