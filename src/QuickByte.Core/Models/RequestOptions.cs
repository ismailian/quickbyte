namespace QuickByte.Core.Models;

/// <summary>
/// Everything about *how* a URL is asked for, as opposed to what happens to the
/// bytes afterwards: the credentials to present, and any extra request headers.
///
/// It travels alongside the URL through both
/// <see cref="Interfaces.IRemoteFileInfoProvider"/> and
/// <see cref="Interfaces.IConnectionFactory"/> so that the probe which resolves
/// a file's size and the eight connections that then fetch it all speak to the
/// server the same way. A probe that authenticates and connections that don't
/// would resolve a size and then download eight copies of a login page.
///
/// <see cref="Headers"/> exists for the browser extension: a link captured from
/// a signed-in page needs that page's <c>Cookie</c> and <c>Referer</c> to
/// resolve to the same file QuickByte's own request would otherwise be refused.
/// </summary>
public sealed class RequestOptions
{
    public static RequestOptions None { get; } = new();

    public DownloadCredentials? Credentials { get; init; }

    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Ask the origin rather than accept whatever a cache in the middle has
    /// (<c>Cache-Control: no-cache</c> plus its HTTP/1.0 twin <c>Pragma</c>).
    ///
    /// It exists for one specific, silent failure. A shared cache that holds a
    /// whole file will happily answer a <c>Range</c> request with the entire
    /// body and a <c>200</c>, while still advertising <c>Accept-Ranges: bytes</c>
    /// — so the file looks segmentable, is split eight ways, and every segment
    /// but the first is handed bytes that belong at offset zero.
    /// <see cref="Services.RemoteFileInfoProvider"/> detects that by re-probing
    /// with these headers, and sets the flag on the download when the origin
    /// turns out to honour ranges perfectly well. It then has to travel with
    /// every connection: a segment that goes back to the cache gets the same
    /// whole-file answer the probe just worked around.
    ///
    /// Off by default, and deliberately not a blanket policy — asking every
    /// server to revalidate would throw away the edge caching that makes a CDN
    /// download fast in the first place.
    /// </summary>
    public bool BypassCache { get; init; }

    public bool HasCredentials => Credentials is { IsEmpty: false };

    public bool HasHeaders => Headers is { Count: > 0 };

    /// <summary>The same request, asked past any intermediary cache.</summary>
    public RequestOptions WithCacheBypass() =>
        BypassCache ? this : new RequestOptions { Credentials = Credentials, Headers = Headers, BypassCache = true };
}
