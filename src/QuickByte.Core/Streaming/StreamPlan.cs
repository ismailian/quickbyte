using QuickByte.Core.Exceptions;

namespace QuickByte.Core.Streaming;

/// <summary>The AES-128 key a piece is encrypted with, and the IV for this piece.</summary>
public sealed record StreamKey(Uri Uri, byte[] Iv);

/// <summary>
/// One request's worth of a stream: an address, the bytes of it to take (all of
/// them when <see cref="Range"/> is null), and how to decrypt them.
/// </summary>
public sealed record StreamPiece(Uri Uri, ByteRange? Range, StreamKey? Key, bool IsInitialization = false);

/// <summary>Every piece of one output file, in the order they are joined.</summary>
public sealed record StreamTrack(IReadOnlyList<StreamPiece> Pieces);

/// <summary>
/// Turns a playlist into the list of pieces to fetch, refusing — with a message a
/// user can read — the streams that cannot be downloaded at all.
///
/// Pure, and separate from the service that fetches the pieces, because this is
/// where a stream is judged: a live playlist that would be downloaded forever, a
/// DRM scheme whose pieces are noise without a licence, a key or a piece at an
/// address that is not the web. Getting any of those wrong does not fail loudly;
/// it produces a file that will not play.
/// </summary>
public static class StreamPlan
{
    /// <summary>More pieces than this in one track is a malformed or hostile playlist, not a video.</summary>
    public const int MaxPieces = DashManifest.MaxSegments;

    // Short enough for the Add window's status line, which is where they are read first.
    public const string LiveMessage = "This is a live stream, which has no end to download to.";

    public const string ProtectedMessage = "This video is DRM-protected, so it cannot be saved.";

    /// <summary>The pieces of an HLS media playlist.</summary>
    /// <exception cref="StreamNotSupportedException">Live, or protected.</exception>
    /// <exception cref="FormatException">A master playlist, an empty one, or a piece that is not on the web.</exception>
    public static StreamTrack ForHls(HlsPlaylist playlist)
    {
        if (playlist.IsMaster) throw new FormatException("This is a list of qualities, not the pieces of one.");
        if (!playlist.IsComplete) throw new StreamNotSupportedException(LiveMessage);
        if (playlist.Segments.Count == 0) throw new FormatException("The playlist lists no pieces.");
        if (playlist.Segments.Count > MaxPieces) throw new FormatException("The playlist lists more pieces than a video could have.");

        var pieces = new List<StreamPiece>(playlist.Segments.Count + 1);
        HlsInit? lastInit = null;

        foreach (var segment in playlist.Segments)
        {
            // A new initialization section goes in front of the first piece it
            // applies to. It changes only at a discontinuity, typically an advert.
            if (segment.Init is { } init && !ReferenceEquals(init, lastInit))
            {
                pieces.Add(new StreamPiece(Web(init.Uri), init.Range, KeyFor(init.Key, segment), IsInitialization: true));
                lastInit = init;
            }

            pieces.Add(new StreamPiece(Web(segment.Uri), segment.Range, KeyFor(segment.Key, segment)));
        }

        return new StreamTrack(pieces);
    }

    /// <summary>The pieces of one representation of a DASH manifest.</summary>
    /// <exception cref="StreamNotSupportedException">Live, or protected.</exception>
    /// <exception cref="FormatException">No such representation, or it lists no pieces.</exception>
    public static StreamTrack ForDash(DashManifest manifest, string representationId)
    {
        if (manifest.IsLive) throw new StreamNotSupportedException(LiveMessage);
        if (manifest.IsProtected) throw new StreamNotSupportedException(ProtectedMessage);

        var representation = manifest.Representations.FirstOrDefault(r => r.Id == representationId)
            ?? throw new FormatException("The video no longer offers the quality this download was started with.");
        if (representation.Segments.Count == 0) throw new FormatException("The manifest lists no pieces for this quality.");

        var pieces = new List<StreamPiece>(representation.Segments.Count + 1);
        if (representation.Initialization is { } init)
            pieces.Add(new StreamPiece(Web(init.Uri), init.Range, null, IsInitialization: true));

        pieces.AddRange(representation.Segments.Select(s => new StreamPiece(Web(s.Uri), s.Range, null)));
        return new StreamTrack(pieces);
    }

    private static StreamKey? KeyFor(HlsKey? key, HlsSegment segment)
    {
        if (key is null) return null;

        // SAMPLE-AES and its relatives encrypt inside the media, under a key a DRM
        // system hands out: FairPlay's skd:// is the common case.
        if (!key.IsAes128) throw new StreamNotSupportedException(ProtectedMessage);
        if (key.Uri is null || !IsWeb(key.Uri)) throw new StreamNotSupportedException(ProtectedMessage);

        return new StreamKey(key.Uri, HlsPlaylist.IvFor(segment with { Key = key }));
    }

    /// <summary>
    /// Only http and https. A playlist is a document from the internet, and a piece
    /// or key it points at <c>file:</c> would have this read the user's own disk.
    /// </summary>
    private static Uri Web(Uri uri) =>
        IsWeb(uri) ? uri : throw new FormatException($"A piece of this stream is not on the web ({uri.Scheme}:).");

    internal static bool IsWeb(Uri uri) =>
        uri.IsAbsoluteUri && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
