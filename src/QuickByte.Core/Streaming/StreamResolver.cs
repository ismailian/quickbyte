using System.Globalization;
using System.Net.Http;
using System.Threading;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Streaming;

/// <summary>One quality a stream can be saved in.</summary>
/// <param name="Label">As offered to the user: "1080p · 5.2 Mbps".</param>
/// <param name="Extension">The main file's extension, with its dot.</param>
/// <param name="ContentType">What the main file will be, for its category and icon.</param>
public sealed record StreamChoice(string Label, long Bandwidth, StreamSource Source, string Extension, string ContentType);

/// <summary>The qualities a stream offers, best first.</summary>
public sealed record StreamDescription(StreamFormat Format, IReadOnlyList<StreamChoice> Choices);

/// <summary>
/// Turns a playlist or manifest address (see <see cref="StreamDetector"/>) into
/// the qualities it offers.
///
/// A master playlist is read, and so is the best quality's own playlist: that is
/// the only place a live stream, a DRM scheme, or the container (MPEG-TS or
/// fragmented MP4, and so the file's extension) shows itself. Refusing a stream in
/// the Add window beats adding a download that fails the moment it runs. The
/// other qualities of the same video are packaged the same way, so one look is
/// taken for all of them; the engine checks each for itself when it runs.
/// </summary>
public sealed class StreamResolver : IStreamResolver
{
    private readonly HttpClient _client;

    public StreamResolver() : this(Services.HttpConnectionFactory.SharedClient) { }

    /// <summary>For the tests: a playlist that is live, protected or oddly packaged cannot be summoned from a real server.</summary>
    internal StreamResolver(HttpClient client) => _client = client;

    public async Task<StreamDescription> DescribeAsync(string url, RequestOptions? options, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new FormatException("This is not a web address.");

        var (text, finalUri) = await StreamFetch.GetTextAsync(_client, uri, options, cancellationToken).ConfigureAwait(false);

        if (HlsPlaylist.LooksLikePlaylist(text))
            return await DescribeHlsAsync(HlsPlaylist.Parse(text, finalUri), finalUri, options, cancellationToken).ConfigureAwait(false);

        if (DashManifest.LooksLikeManifest(text))
            return DescribeDash(DashManifest.Parse(text, finalUri));

        throw new FormatException("This address is not a video playlist QuickByte can read.");
    }

    // ------------------------------------------------------------------ HLS --

    private async Task<StreamDescription> DescribeHlsAsync(
        HlsPlaylist playlist, Uri playlistUri, RequestOptions? options, CancellationToken cancellationToken)
    {
        if (!playlist.IsMaster)
        {
            // A single quality: the address is its own media playlist.
            var track = StreamPlan.ForHls(playlist);
            var source = new StreamSource { Format = StreamFormat.Hls, MainPlaylistUrl = playlistUri.AbsoluteUri, Quality = "Original" };
            var (extension, type) = HlsContainer(playlist, track, audioOnly: false);
            return new StreamDescription(StreamFormat.Hls, new[] { new StreamChoice("Original", 0, source, extension, type) });
        }

        var variants = playlist.Variants
            .Where(v => StreamPlan.IsWeb(v.Uri))
            .OrderByDescending(v => v.Bandwidth)
            .GroupBy(v => (v.Resolution, v.Bandwidth, AudioFor(playlist, v)?.AbsoluteUri))
            .Select(g => g.First())
            .ToList();
        if (variants.Count == 0) throw new FormatException("The playlist lists no qualities.");

        // The best quality is read, and its sound if separate: that is where a live
        // or protected stream, and the container, show themselves.
        var best = variants[0];
        var bestMedia = await ReadMediaAsync(best.Uri, options, cancellationToken).ConfigureAwait(false);
        var (videoExtension, videoType) = HlsContainer(bestMedia, StreamPlan.ForHls(bestMedia), audioOnly: IsAudioOnly(best));

        var bestAudio = AudioFor(playlist, best);
        (string Extension, string ContentType)? audioContainer = null;
        if (bestAudio is not null)
        {
            var audioMedia = await ReadMediaAsync(bestAudio, options, cancellationToken).ConfigureAwait(false);
            audioContainer = HlsContainer(audioMedia, StreamPlan.ForHls(audioMedia), audioOnly: true);
        }
        string? audioExtension = audioContainer?.Extension;

        // The same picture offered with different sound — AAC, AC-3, E-AC-3 — reads
        // as the same line three times unless the label says which is which.
        bool severalSounds = variants.Select(v => AudioFor(playlist, v)?.AbsoluteUri).Distinct().Count() > 1;

        var choices = variants.Select(variant =>
        {
            var audio = AudioFor(playlist, variant);
            string label = LabelFor(Height(variant.Resolution), variant.Bandwidth, IsAudioOnly(variant));
            if (severalSounds && AudioCodecName(variant.Codecs) is { } codec) label += $" · {codec}";
            var source = new StreamSource
            {
                Format = StreamFormat.Hls,
                MainPlaylistUrl = variant.Uri.AbsoluteUri,
                AudioPlaylistUrl = audio?.AbsoluteUri,
                AudioExtension = audio is null ? null : audioExtension ?? ".m4a",
                Quality = label
            };
            // Sound alone in fragmented MP4 is an .m4a; in MPEG-TS it is still a .ts.
            var (extension, type) = IsAudioOnly(variant) && videoExtension == ".mp4"
                ? (".m4a", "audio/mp4")
                : (videoExtension, videoType);
            return new StreamChoice(label, variant.Bandwidth, source, extension, type);
        }).ToList();

        // The sound on its own, for a talk or a concert — offered when it is a
        // playlist of its own and no quality already is only sound.
        if (bestAudio is not null && audioContainer is { } container && !variants.Any(IsAudioOnly))
        {
            string label = severalSounds && AudioCodecName(best.Codecs) is { } codec ? $"Audio only · {codec}" : "Audio only";
            choices.Add(new StreamChoice(
                label, 0,
                new StreamSource { Format = StreamFormat.Hls, MainPlaylistUrl = bestAudio.AbsoluteUri, Quality = label },
                container.Extension, container.ContentType));
        }

        return new StreamDescription(StreamFormat.Hls, choices);
    }

    private async Task<HlsPlaylist> ReadMediaAsync(Uri uri, RequestOptions? options, CancellationToken cancellationToken)
    {
        var (text, finalUri) = await StreamFetch.GetTextAsync(_client, uri, options, cancellationToken).ConfigureAwait(false);
        return HlsPlaylist.Parse(text, finalUri);
    }

    /// <summary>The separate sound a variant plays with: its group's default rendition that has a playlist, else the first that does.</summary>
    internal static Uri? AudioFor(HlsPlaylist master, HlsVariant variant)
    {
        if (string.IsNullOrEmpty(variant.AudioGroup)) return null;

        var group = master.Renditions
            .Where(r => r.Type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase) && r.GroupId == variant.AudioGroup && r.Uri is not null)
            .Where(r => StreamPlan.IsWeb(r.Uri!))
            .ToList();

        // No playlist in the group means the sound is inside the variant's own pieces.
        return (group.FirstOrDefault(r => r.IsDefault) ?? group.FirstOrDefault())?.Uri;
    }

    /// <summary>The sound codec named in a variant's CODECS, as a person would name it.</summary>
    internal static string? AudioCodecName(string? codecs)
    {
        foreach (string codec in (codecs ?? string.Empty).Split(','))
        {
            string c = codec.Trim().ToLowerInvariant();
            if (c.StartsWith("mp4a.40.34")) return "MP3";
            if (c.StartsWith("mp4a")) return "AAC";
            if (c.StartsWith("ac-3")) return "AC-3";
            if (c.StartsWith("ec-3")) return "E-AC-3";
            if (c.StartsWith("opus")) return "Opus";
            if (c.StartsWith("flac")) return "FLAC";
        }
        return null;
    }

    private static bool IsAudioOnly(HlsVariant variant) =>
        variant.Resolution is null
        && variant.Codecs is { } codecs
        && codecs.Split(',').All(c => c.Trim().StartsWith("mp4a", StringComparison.OrdinalIgnoreCase)
                                      || c.Trim().StartsWith("ac-3", StringComparison.OrdinalIgnoreCase)
                                      || c.Trim().StartsWith("ec-3", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What joining a playlist's pieces produces. An initialization section means
    /// fragmented MP4, which plays as an .mp4; otherwise the pieces' own extension
    /// says (packed audio is .aac), and MPEG-TS is the default HLS has always had.
    /// </summary>
    internal static (string Extension, string ContentType) HlsContainer(HlsPlaylist media, StreamTrack track, bool audioOnly)
    {
        if (media.IsFragmentedMp4) return audioOnly ? (".m4a", "audio/mp4") : (".mp4", "video/mp4");

        string pieceExtension = Path.GetExtension(track.Pieces[^1].Uri.AbsolutePath).ToLowerInvariant();
        return pieceExtension switch
        {
            ".aac" => (".aac", "audio/aac"),
            ".mp3" => (".mp3", "audio/mpeg"),
            ".ac3" => (".ac3", "audio/ac3"),
            ".m4s" or ".mp4" => audioOnly ? (".m4a", "audio/mp4") : (".mp4", "video/mp4"),
            _ => (".ts", "video/mp2t")
        };
    }

    // ----------------------------------------------------------------- DASH --

    private static StreamDescription DescribeDash(DashManifest manifest)
    {
        if (manifest.IsLive) throw new StreamNotSupportedException(StreamPlan.LiveMessage);
        if (manifest.IsProtected) throw new StreamNotSupportedException(StreamPlan.ProtectedMessage);

        var usable = manifest.Representations
            .Where(r => r.Segments.Count > 0 && r.Segments.All(s => StreamPlan.IsWeb(s.Uri)))
            .ToList();

        var videos = usable.Where(r => IsKind(r, "video")).OrderByDescending(r => r.Bandwidth).ToList();
        var audios = usable.Where(r => IsKind(r, "audio")).OrderByDescending(r => r.Bandwidth).ToList();
        var bestAudio = audios.FirstOrDefault();

        var choices = new List<StreamChoice>();
        foreach (var video in videos.GroupBy(v => (v.Height, v.Bandwidth)).Select(g => g.First()))
        {
            string label = LabelFor(video.Height, video.Bandwidth, audioOnly: false);
            choices.Add(new StreamChoice(
                label, video.Bandwidth,
                new StreamSource
                {
                    Format = StreamFormat.Dash,
                    MainRepresentation = video.Id,
                    AudioRepresentation = bestAudio?.Id,
                    AudioExtension = bestAudio is null ? null : DashExtension(bestAudio),
                    Quality = label
                },
                DashExtension(video),
                video.MimeType.Length > 0 ? video.MimeType : "video/mp4"));
        }

        if (bestAudio is not null)
        {
            string label = LabelFor(0, bestAudio.Bandwidth, audioOnly: true);
            choices.Add(new StreamChoice(
                label, bestAudio.Bandwidth,
                new StreamSource { Format = StreamFormat.Dash, MainRepresentation = bestAudio.Id, Quality = label },
                DashExtension(bestAudio),
                bestAudio.MimeType.Length > 0 ? bestAudio.MimeType : "audio/mp4"));
        }

        if (choices.Count == 0) throw new FormatException("The manifest offers nothing that can be downloaded.");
        return new StreamDescription(StreamFormat.Dash, choices);
    }

    private static bool IsKind(DashRepresentation representation, string kind) =>
        representation.ContentType.Equals(kind, StringComparison.OrdinalIgnoreCase)
        || representation.MimeType.StartsWith(kind + "/", StringComparison.OrdinalIgnoreCase);

    internal static string DashExtension(DashRepresentation representation)
    {
        bool audio = IsKind(representation, "audio") && !IsKind(representation, "video");
        return representation.MimeType.EndsWith("/webm", StringComparison.OrdinalIgnoreCase)
            ? ".webm"
            : audio ? ".m4a" : ".mp4";
    }

    // --------------------------------------------------------------- labels --

    private static int Height(string? resolution)
    {
        // RESOLUTION=1920x1080
        string[] parts = (resolution ?? string.Empty).Split('x', 'X');
        return parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height) ? height : 0;
    }

    internal static string LabelFor(int height, long bandwidth, bool audioOnly)
    {
        var parts = new List<string>();
        if (audioOnly) parts.Add("Audio only");
        else if (height > 0) parts.Add($"{height}p");
        if (bandwidth > 0) parts.Add(Bitrate(bandwidth));
        return parts.Count == 0 ? "Default" : string.Join(" · ", parts);
    }

    internal static string Bitrate(long bitsPerSecond) =>
        bitsPerSecond >= 1_000_000
            ? $"{(bitsPerSecond / 1_000_000.0).ToString("0.#", CultureInfo.CurrentCulture)} Mbps"
            : $"{Math.Max(1, bitsPerSecond / 1000).ToString(CultureInfo.CurrentCulture)} kbps";
}
