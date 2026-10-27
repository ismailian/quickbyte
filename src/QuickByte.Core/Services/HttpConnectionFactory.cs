using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.Core.Services.Proxy;

namespace QuickByte.Core.Services;

/// <summary>
/// Concrete <see cref="IConnectionFactory"/> that produces HTTP-based
/// <see cref="DownloadConnection"/> workers, all sharing a single static
/// <see cref="HttpClient"/> (best practice: avoid socket exhaustion from
/// creating a new HttpClient per connection).
/// </summary>
public sealed class HttpConnectionFactory : IConnectionFactory
{
    internal static readonly HttpClient SharedClient = CreateClient();

    /// <summary>
    /// QuickByte's own User-Agent. The value actually sent is
    /// <see cref="AppUserAgent.Current"/>, written per request by
    /// <see cref="HttpRequestDecorator"/> — this is the client-wide backstop, and
    /// it is a fixed string on purpose: re-stamping these defaults when Options
    /// changes would race every request in flight on a client shared by every
    /// connection of every download. See <see cref="AppUserAgent"/>.
    /// </summary>
    internal const string UserAgent = AppUserAgent.Default;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(CreateHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan // per-request cancellation handled via CancellationToken
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    /// <summary>
    /// The handler every download client is built on — the shared one, and each
    /// signed-in one <see cref="AuthenticatedHttp"/> makes — so a download that needs
    /// NTLM goes through the same proxy with the same cookie rules as any other.
    /// </summary>
    internal static SocketsHttpHandler CreateHandler() =>
        new()
        {
            MaxConnectionsPerServer = DownloadSettings.MaxConnections + 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),

            // Asked for every request, so a proxy changed in Options applies to
            // the next one without a restart. See AppProxy.
            Proxy = AppProxy.Shared,
            UseProxy = true,

            // The browser extension hands us the page's own Cookie header, and a
            // container that also injects its own would leave two Cookie sources
            // fighting over one request. Nothing here needs a cookie jar.
            UseCookies = false
        };

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
