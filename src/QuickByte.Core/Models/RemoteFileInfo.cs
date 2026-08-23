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
    public bool SupportsRangeRequests { get; set; }
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
