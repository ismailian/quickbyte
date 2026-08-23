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

    public bool HasCredentials => Credentials is { IsEmpty: false };

    public bool HasHeaders => Headers is { Count: > 0 };
}
