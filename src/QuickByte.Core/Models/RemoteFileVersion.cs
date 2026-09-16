namespace QuickByte.Core.Models;

/// <summary>
/// Which version of a remote file a set of chunks belongs to: the server's
/// validators for it, and the host that issued them.
///
/// Resume trusts the bytes already on disk to be the head of the same file the
/// server is about to send the rest of. That holds only while the file stays the
/// same, and nothing about a paused download guarantees it does — a nightly build
/// republished under the same URL, or an installer replaced by the next patch
/// release, is not an error anywhere in HTTP. The chunks from the old version and
/// the tail of the new one are merged into a file that is neither, silently.
/// These values are what let a resume notice.
/// </summary>
/// <remarks>
/// <see cref="Host"/> is part of the version because a validator only means
/// something to the server that minted it. A mirror redirector sends each request
/// to whichever mirror it picks, and two mirrors serving the identical file will
/// happily compute different ETags for it. Comparing across hosts would read that
/// as a changed file and throw away a download that was fine.
/// </remarks>
public sealed record RemoteFileVersion
{
    /// <summary>
    /// The <c>ETag</c> exactly as the server sent it, including a <c>W/</c>
    /// prefix: a weak tag may be compared, but it must never be sent in
    /// <c>If-Range</c> (RFC 9110 §13.1.5).
    /// </summary>
    public string? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    /// <summary>Authority (host and port) of the address that answered, after redirects.</summary>
    public string? Host { get; init; }

    public bool HasValidator => ETag is not null || LastModified is not null;

    public bool HasStrongETag => ETag is not null && !ETag.StartsWith("W/", StringComparison.Ordinal);
}
