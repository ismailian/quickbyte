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

    public bool HasKnownSize => ContentLength > 0;
}
