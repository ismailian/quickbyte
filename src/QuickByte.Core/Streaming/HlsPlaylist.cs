using System.Globalization;

namespace QuickByte.Core.Streaming;

/// <summary>A byte range within a resource: <c>Length</c> bytes from <c>Offset</c>.</summary>
public readonly record struct ByteRange(long Offset, long Length)
{
    public long End => Offset + Length - 1;
}

/// <summary>How a segment is encrypted in an HLS playlist.</summary>
public sealed record HlsKey(string Method, Uri? Uri, byte[]? Iv)
{
    public bool IsAes128 => Method.Equals("AES-128", StringComparison.OrdinalIgnoreCase);
    public bool IsNone => Method.Equals("NONE", StringComparison.OrdinalIgnoreCase);
}

public sealed record HlsSegment(Uri Uri, double Duration, ByteRange? Range, HlsKey? Key, long SequenceNumber, HlsInit? Init);

/// <summary>
/// An <c>#EXT-X-MAP</c> initialization section, which fragmented-MP4 segments need
/// in front of them. <paramref name="Key"/> is the key in force where the tag
/// appears: RFC 8216 encrypts the section with it, with an explicit IV.
/// </summary>
public sealed record HlsInit(Uri Uri, ByteRange? Range, HlsKey? Key);

public sealed record HlsVariant(Uri Uri, long Bandwidth, string? Resolution, string? Codecs, string? AudioGroup);

public sealed record HlsRendition(string Type, string GroupId, string Name, Uri? Uri, bool IsDefault);

/// <summary>
/// An HLS playlist (RFC 8216): either a master playlist naming the qualities a
/// video is offered in, or a media playlist listing the segments of one of them.
///
/// Only what a download needs is kept. Timing, discontinuities and program dates
/// matter to a player; a download concatenates the segments in order, which is how
/// an HLS transport stream is meant to be joined.
/// </summary>
public sealed class HlsPlaylist
{
    public bool IsMaster => Variants.Count > 0;

    public List<HlsVariant> Variants { get; } = new();
    public List<HlsRendition> Renditions { get; } = new();

    public List<HlsSegment> Segments { get; } = new();

    /// <summary><c>#EXT-X-ENDLIST</c>: the playlist is complete. Without it the stream is live and never ends.</summary>
    public bool IsComplete { get; private set; }

    /// <summary>Segments come with <c>#EXT-X-MAP</c>: fragmented MP4 rather than MPEG-TS.</summary>
    public bool IsFragmentedMp4 => Segments.Any(s => s.Init is not null);

    public static bool LooksLikePlaylist(string text) =>
        text.TrimStart((char)0xFEFF, ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal);

    /// <exception cref="FormatException">Not an HLS playlist.</exception>
    public static HlsPlaylist Parse(string text, Uri baseUri)
    {
        if (!LooksLikePlaylist(text)) throw new FormatException("This is not an HLS playlist (it does not start with #EXTM3U).");

        var playlist = new HlsPlaylist();
        long sequence = 0;
        double duration = 0;
        ByteRange? range = null;
        long nextRangeOffset = 0;
        HlsKey? key = null;
        HlsInit? init = null;
        HlsVariantPending? variant = null;

        // A byte order mark is not whitespace to Trim, and left in place it turns
        // the header line into the address of a piece.
        foreach (string raw in text.TrimStart((char)0xFEFF).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;

            if (!line.StartsWith('#'))
            {
                var uri = Resolve(baseUri, line);
                if (variant is not null)
                {
                    playlist.Variants.Add(new HlsVariant(uri, variant.Bandwidth, variant.Resolution, variant.Codecs, variant.Audio));
                    variant = null;
                }
                else
                {
                    playlist.Segments.Add(new HlsSegment(uri, duration, range, key, sequence, init));
                    if (range is { } used) nextRangeOffset = used.Offset + used.Length;
                    sequence++;
                    duration = 0;
                    range = null;
                }
                continue;
            }

            int colon = line.IndexOf(':');
            string tag = colon < 0 ? line : line[..colon];
            string value = colon < 0 ? string.Empty : line[(colon + 1)..];

            switch (tag)
            {
                case "#EXT-X-STREAM-INF":
                {
                    var attributes = Attributes(value);
                    variant = new HlsVariantPending(
                        long.TryParse(attributes.GetValueOrDefault("BANDWIDTH"), NumberStyles.None, CultureInfo.InvariantCulture, out long bandwidth) ? bandwidth : 0,
                        attributes.GetValueOrDefault("RESOLUTION"),
                        attributes.GetValueOrDefault("CODECS"),
                        attributes.GetValueOrDefault("AUDIO"));
                    break;
                }
                case "#EXT-X-MEDIA":
                {
                    var attributes = Attributes(value);
                    playlist.Renditions.Add(new HlsRendition(
                        attributes.GetValueOrDefault("TYPE") ?? string.Empty,
                        attributes.GetValueOrDefault("GROUP-ID") ?? string.Empty,
                        attributes.GetValueOrDefault("NAME") ?? string.Empty,
                        attributes.TryGetValue("URI", out string? uri) ? Resolve(baseUri, uri) : null,
                        string.Equals(attributes.GetValueOrDefault("DEFAULT"), "YES", StringComparison.OrdinalIgnoreCase)));
                    break;
                }
                case "#EXT-X-MEDIA-SEQUENCE":
                    long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
                    break;
                case "#EXTINF":
                    double.TryParse(value.Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
                    break;
                case "#EXT-X-BYTERANGE":
                    range = ParseRange(value, nextRangeOffset);
                    break;
                case "#EXT-X-KEY":
                {
                    var attributes = Attributes(value);
                    string method = attributes.GetValueOrDefault("METHOD") ?? "NONE";
                    key = method.Equals("NONE", StringComparison.OrdinalIgnoreCase)
                        ? null
                        : new HlsKey(
                            method,
                            attributes.TryGetValue("URI", out string? keyUri) ? Resolve(baseUri, keyUri) : null,
                            attributes.TryGetValue("IV", out string? iv) ? ParseIv(iv) : null);
                    break;
                }
                case "#EXT-X-MAP":
                {
                    var attributes = Attributes(value);
                    if (attributes.TryGetValue("URI", out string? mapUri))
                    {
                        init = new HlsInit(
                            Resolve(baseUri, mapUri),
                            attributes.TryGetValue("BYTERANGE", out string? mapRange) ? ParseRange(mapRange, 0) : null,
                            key);
                    }
                    break;
                }
                case "#EXT-X-ENDLIST":
                    playlist.IsComplete = true;
                    break;
            }
        }

        return playlist;
    }

    /// <summary>
    /// The IV an AES-128 segment is decrypted with: the one the playlist gives, else
    /// the segment's media sequence number as a 128-bit big-endian integer (RFC 8216 §5.2).
    /// </summary>
    public static byte[] IvFor(HlsSegment segment)
    {
        if (segment.Key?.Iv is { Length: 16 } iv) return iv;

        var bytes = new byte[16];
        long number = segment.SequenceNumber;
        for (int i = 15; i >= 8; i--)
        {
            bytes[i] = (byte)(number & 0xFF);
            number >>= 8;
        }
        return bytes;
    }

    /// <summary>
    /// An attribute list: <c>KEY=VALUE</c> pairs separated by commas, where a quoted
    /// value may itself contain commas (a CODECS list always does).
    /// </summary>
    internal static Dictionary<string, string> Attributes(string list)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (i < list.Length)
        {
            int equals = list.IndexOf('=', i);
            if (equals < 0) break;
            string name = list[i..equals].Trim().TrimStart(',').Trim();

            int start = equals + 1;
            string value;
            if (start < list.Length && list[start] == '"')
            {
                int close = list.IndexOf('"', start + 1);
                if (close < 0) close = list.Length;
                value = list[(start + 1)..close];
                i = close + 1;
            }
            else
            {
                int comma = list.IndexOf(',', start);
                if (comma < 0) comma = list.Length;
                value = list[start..comma].Trim();
                i = comma;
            }

            if (name.Length > 0) attributes[name] = value;
            while (i < list.Length && list[i] == ',') i++;
        }
        return attributes;
    }

    private static ByteRange? ParseRange(string value, long defaultOffset)
    {
        string[] parts = value.Trim('"').Split('@');
        if (!long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out long length) || length <= 0) return null;

        long offset = defaultOffset;
        if (parts.Length > 1 && !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out offset)) return null;
        return new ByteRange(offset, length);
    }

    private static byte[]? ParseIv(string value)
    {
        string hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
        if (hex.Length != 32) return null;
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }

    private static Uri Resolve(Uri baseUri, string reference) => new(baseUri, reference.Trim());

    private sealed record HlsVariantPending(long Bandwidth, string? Resolution, string? Codecs, string? Audio);
}
