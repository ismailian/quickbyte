using System.Threading;
using System.Net.Http;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// Concrete <see cref="IConnectionFactory"/> that produces HTTP-based
/// <see cref="DownloadConnection"/> workers, all sharing a single static
/// <see cref="HttpClient"/> (best practice: avoid socket exhaustion from
/// creating a new HttpClient per connection).
/// </summary>
public sealed class HttpConnectionFactory : IConnectionFactory
{
    private static readonly HttpClient SharedClient = CreateClient();

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = DownloadSettings.MaxConnections + 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),

            // The browser extension hands us the page's own Cookie header, and a
            // container that also injects its own would leave two Cookie sources
            // fighting over one request. Nothing here needs a cookie jar.
            UseCookies = false
        };
        var client = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan // per-request cancellation handled via CancellationToken
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("QuickByte/1.0 (+https://github.com/quickbyte)");
        return client;
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
        return new DownloadConnection(
            SharedClient, connectionId, url, rangeStart, rangeEnd, alreadyDownloaded, chunkFilePath,
            settings, bandwidthLimiter, options);
    }
}
