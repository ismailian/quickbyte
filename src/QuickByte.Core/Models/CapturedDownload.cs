namespace QuickByte.Core.Models;

/// <summary>
/// A download the browser extension intercepted and handed to QuickByte over
/// the loopback bridge (<see cref="Interfaces.IBrowserIntegrationService"/>).
///
/// It carries more than a URL because a link clicked inside a signed-in page is
/// often only fetchable *as that page*: the cookie jar, the referring page and
/// the browser's own user agent are what make QuickByte's request resolve to
/// the same bytes Chrome would have downloaded. <see cref="FileName"/> and
/// <see cref="TotalBytes"/> come from Chrome's own download record, so they are
/// already correct even for a URL that only answers a HEAD with a redirect.
/// </summary>
public sealed record CapturedDownload
{
    public string Url { get; init; } = string.Empty;
    public string? FileName { get; init; }
    public long TotalBytes { get; init; }
    public string? MimeType { get; init; }
    public string? Referrer { get; init; }
    public string? UserAgent { get; init; }
    public string? Cookie { get; init; }

    /// <summary>
    /// The browser-supplied headers as <see cref="RequestOptions"/> wants them.
    /// Empty entries are dropped rather than sent blank — an empty
    /// <c>Referer</c> is a different request from no <c>Referer</c> at all.
    /// </summary>
    public Dictionary<string, string> ToHeaders()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Cookie)) headers["Cookie"] = Cookie!;
        if (!string.IsNullOrWhiteSpace(Referrer)) headers["Referer"] = Referrer!;
        if (!string.IsNullOrWhiteSpace(UserAgent)) headers["User-Agent"] = UserAgent!;
        return headers;
    }
}
