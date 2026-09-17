namespace QuickByte.Core.Models;

/// <summary>The two adaptive-streaming formats a video player fetches in pieces.</summary>
public enum StreamFormat
{
    Hls = 0,
    Dash = 1
}

/// <summary>
/// What makes a download a streaming video rather than a file: which format its
/// address (<see cref="DownloadItem.Url"/>, the playlist or manifest) is in, and
/// which of the qualities it offers was chosen.
///
/// A video player never fetches "the video". It reads a playlist, picks a quality,
/// and fetches that quality's pieces one after another, often with the sound in a
/// separate playlist of its own. So this names the pieces' playlists (HLS) or
/// representations (DASH) rather than a file, and the engine re-reads the playlist
/// on every run — signed piece addresses expire, and the playlist is what hands out
/// fresh ones.
///
/// Persisted on the item, like <see cref="DownloadItem.Credentials"/>: a resume
/// three days later has to fetch the same quality it started with, or the pieces
/// on disk belong to a different video.
/// </summary>
public sealed record StreamSource
{
    public StreamFormat Format { get; init; }

    /// <summary>HLS: the media playlist of the chosen quality — the video, or video and sound together.</summary>
    public string? MainPlaylistUrl { get; init; }

    /// <summary>HLS: the separate sound playlist the chosen quality plays with, if it has one.</summary>
    public string? AudioPlaylistUrl { get; init; }

    /// <summary>DASH: the id of the chosen representation.</summary>
    public string? MainRepresentation { get; init; }

    /// <summary>DASH: the id of the separate sound representation, if there is one.</summary>
    public string? AudioRepresentation { get; init; }

    /// <summary>
    /// The extension the sound is saved with. There is no muxer here, so separate
    /// video and sound are saved as two files side by side — see <see cref="AudioFileName"/>.
    /// </summary>
    public string? AudioExtension { get; init; }

    /// <summary>
    /// Where the sound is saved, beside <see cref="DownloadItem.FileName"/> in the same
    /// folder. Chosen when the download is added, under the same rule as the main
    /// file's name, so two downloads can never write one sound file.
    /// </summary>
    public string? AudioFileName { get; init; }

    /// <summary>What the user chose, as it was offered — "1080p · 5.2 Mbps".</summary>
    public string Quality { get; init; } = string.Empty;

    public bool HasSeparateAudio =>
        Format == StreamFormat.Hls ? AudioPlaylistUrl is not null : AudioRepresentation is not null;
}
