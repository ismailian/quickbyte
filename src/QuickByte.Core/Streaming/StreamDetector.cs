namespace QuickByte.Core.Streaming;

/// <summary>
/// Recognises the address of a streaming video's playlist or manifest — the one
/// question the Add window asks before it offers a choice of quality instead of
/// saving a text file of addresses.
/// </summary>
public static class StreamDetector
{
    private static readonly string[] PlaylistTypes =
    {
        "application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl", "audio/x-mpegurl"
    };

    /// <summary>
    /// By its content type or, when the server does not say (or says
    /// <c>text/plain</c>, which many do), by its extension.
    /// </summary>
    public static bool IsStream(string url, string? contentType)
    {
        string type = (contentType ?? string.Empty).Split(';')[0].Trim();
        if (PlaylistTypes.Contains(type, StringComparer.OrdinalIgnoreCase)) return true;
        if (type.Equals("application/dash+xml", StringComparison.OrdinalIgnoreCase)) return true;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        string extension = Path.GetExtension(uri.AbsolutePath);
        return extension.Equals(".m3u8", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".mpd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The names players give every playlist, which say nothing about the video.</summary>
    private static readonly HashSet<string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "master", "index", "playlist", "manifest", "main", "stream", "chunklist", "prog_index",
        "video", "media", "hls", "dash", "output", "source", "default"
    };

    /// <summary>
    /// A name for the saved video, with <paramref name="extension"/>. A playlist is
    /// nearly always called <c>master.m3u8</c> or <c>manifest.mpd</c>, and a hundred
    /// videos saved as <c>master (99).mp4</c> are a hundred files nobody can tell
    /// apart — so a generic name gives way to the folder the playlist sits in, which
    /// is usually the video's own (<c>/talks/keynote-2026/master.m3u8</c>).
    /// </summary>
    public static string SuggestFileName(string url, string currentName, string extension)
    {
        string stem = Path.GetFileNameWithoutExtension(currentName);

        if ((stem.Length == 0 || GenericNames.Contains(stem))
            && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            string? folder = uri.Segments
                .Take(uri.Segments.Length - 1)
                .Select(s => Uri.UnescapeDataString(s.Trim('/')))
                .LastOrDefault(IsDescriptive);
            stem = folder ?? "video";
        }

        if (stem.Length == 0) stem = "video";
        return Helpers.FileNameHelper.SanitizeFileName(stem + extension);
    }

    /// <summary>Not empty, not generic, and not a bare number or hash — "1080", "v1", "a3f9c0e1...".</summary>
    private static bool IsDescriptive(string segment) =>
        segment.Length >= 3
        && !GenericNames.Contains(segment)
        && segment.Count(char.IsLetter) >= 3
        && !(segment.Length >= 16 && segment.All(Uri.IsHexDigit));
}
