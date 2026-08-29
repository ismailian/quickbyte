namespace QuickByte.Core.Models;

/// <summary>
/// Metadata resolved from the remote server before a download starts:
/// file name, size, content type and whether byte-range (partial content)
/// requests are supported, which decides if we can use multiple connections.
/// </summary>
public sealed class RemoteFileInfo
{
    public string FileName { get; set; } = "download";
    public long ContentLength { get; set; }
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>
    /// True only once a ranged request has actually come back as
    /// <c>206 Partial Content</c> from the offset it asked for. It is never set
    /// from an <c>Accept-Ranges</c> header alone — see
    /// <see cref="ServerClaimsRangeSupport"/> for why that claim cannot be
    /// trusted, and note which way the two errors cut: guessing "no" costs a
    /// download some speed, guessing "yes" costs it every segment but the first.
    /// </summary>
    public bool SupportsRangeRequests { get; set; }

    /// <summary>
    /// What the server *said* — <c>Accept-Ranges: bytes</c> on the HEAD or the
    /// probe — as opposed to what it did.
    ///
    /// The two disagree more often than the header suggests, because the thing
    /// answering is frequently not the origin. A CDN edge holding the whole file
    /// will answer a <c>Range</c> request with the entire body and a <c>200</c>
    /// while passing the origin's <c>Accept-Ranges: bytes</c> straight through.
    /// Splitting a file on the strength of that claim is what leaves seven of
    /// eight connections holding a response that starts at byte zero.
    ///
    /// Kept because the contradiction is worth acting on rather than just
    /// disbelieving: a server that claims ranges and then ignores one is exactly
    /// the case where re-asking past the cache
    /// (<see cref="RequestOptions.BypassCache"/>) is likely to work.
    /// </summary>
    public bool ServerClaimsRangeSupport { get; set; }

    /// <summary>
    /// Set when this URL only honours ranges once the request asks the origin
    /// directly. It travels onto the <see cref="DownloadItem"/> so every
    /// connection asks the same way the probe that resolved this did.
    /// </summary>
    public bool BypassCache { get; set; }

    public string? ETag { get; set; }
    public DateTimeOffset? LastModified { get; set; }
    public string FinalUrl { get; set; } = string.Empty;

    /// <summary>
    /// Set when the server answered the probe with a challenge (HTTP 401, FTP
    /// 530) instead of metadata. The Add Download dialog reads it to swap its
    /// "could not fetch" message for a credentials prompt, which is the only
    /// thing that will actually get the user any further.
    /// </summary>
    public bool RequiresAuthentication { get; set; }

    public bool HasKnownSize => ContentLength > 0;
}
