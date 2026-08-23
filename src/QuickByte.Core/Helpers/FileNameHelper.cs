namespace QuickByte.Core.Helpers;

/// <summary>Filename derivation and collision-avoidance utilities.</summary>
public static class FileNameHelper
{
    /// <summary>
    /// Content types worth turning into an extension. Deliberately a short,
    /// unambiguous list rather than a full MIME registry: this only ever fires
    /// when the URL yielded no extension at all, and guessing wrong there
    /// produces a file Explorer refuses to open with anything sensible.
    /// <c>application/octet-stream</c> is absent on purpose — it means "bytes",
    /// which is exactly what we already knew.
    /// </summary>
    private static readonly Dictionary<string, string> ExtensionByContentType = new(StringComparer.OrdinalIgnoreCase)
    {
        ["application/epub+zip"] = "epub",
        ["application/gzip"] = "gz",
        ["application/java-archive"] = "jar",
        ["application/json"] = "json",
        ["application/pdf"] = "pdf",
        ["application/vnd.android.package-archive"] = "apk",
        ["application/vnd.debian.binary-package"] = "deb",
        ["application/vnd.microsoft.portable-executable"] = "exe",
        ["application/vnd.rar"] = "rar",
        ["application/x-7z-compressed"] = "7z",
        ["application/x-apple-diskimage"] = "dmg",
        ["application/x-bzip2"] = "bz2",
        ["application/x-gzip"] = "gz",
        ["application/x-iso9660-image"] = "iso",
        ["application/x-msdownload"] = "exe",
        ["application/x-ms-installer"] = "msi",
        ["application/x-msi"] = "msi",
        ["application/x-rar-compressed"] = "rar",
        ["application/x-rpm"] = "rpm",
        ["application/x-tar"] = "tar",
        ["application/x-xz"] = "xz",
        ["application/zip"] = "zip",
        ["audio/flac"] = "flac",
        ["audio/mp4"] = "m4a",
        ["audio/mpeg"] = "mp3",
        ["audio/ogg"] = "ogg",
        ["audio/wav"] = "wav",
        ["image/gif"] = "gif",
        ["image/jpeg"] = "jpg",
        ["image/png"] = "png",
        ["image/svg+xml"] = "svg",
        ["image/webp"] = "webp",
        ["text/csv"] = "csv",
        ["text/html"] = "html",
        ["text/plain"] = "txt",
        ["video/mp4"] = "mp4",
        ["video/quicktime"] = "mov",
        ["video/webm"] = "webm",
        ["video/x-matroska"] = "mkv",
        ["video/x-msvideo"] = "avi"
    };

    public static string SanitizeFileName(string name)
    {
        // A server-supplied name may carry a path — deliberately, in the case of
        // a "../../autorun.inf" — and the caller wants a file name, not a route
        // out of the download folder.
        int separator = name.LastIndexOfAny(new[] { '/', '\\' });
        if (separator >= 0) name = name[(separator + 1)..];

        name = name.Trim().Trim('"');

        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        // "." and ".." survive the loop above intact and are not file names.
        if (name.Length == 0 || name.All(c => c == '.')) return "download";
        return name;
    }

    /// <summary>
    /// Derives a file name from a URL's path, ignoring query and fragment.
    /// </summary>
    /// <remarks>
    /// Callers must pass the URL <em>after</em> redirects. A great many download
    /// links are an opaque endpoint that redirects to the real file —
    /// SourceForge's <c>.../7z2301-x64.exe/download</c> being the canonical
    /// example — and naming the file from the address the user pasted produces
    /// "download".
    ///
    /// When the last segment carries no extension, earlier segments are tried in
    /// turn, which recovers the name from that same shape without a redirect
    /// having to happen at all. The check for what counts as an extension is
    /// deliberately strict, so a version number in a path (<c>/v1.2/get</c>)
    /// isn't mistaken for a file.
    /// </remarks>
    public static string FileNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            string[] segments = uri.LocalPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

            for (int i = segments.Length - 1; i >= 0; i--)
            {
                string candidate = Uri.UnescapeDataString(segments[i]);
                if (LooksLikeFileName(candidate)) return SanitizeFileName(candidate);
            }

            return segments.Length > 0
                ? SanitizeFileName(Uri.UnescapeDataString(segments[^1]))
                : "download";
        }
        catch
        {
            return "download";
        }
    }

    /// <summary>
    /// Whether a path segment reads as a file rather than as a directory. Wants a
    /// dot with 2–8 trailing characters that are alphanumeric and include at
    /// least one letter — enough for <c>.mp4</c>, <c>.7z</c> and <c>.tar.gz</c>,
    /// and not enough for <c>v1.2</c>.
    /// </summary>
    private static bool LooksLikeFileName(string segment)
    {
        int dot = segment.LastIndexOf('.');
        if (dot <= 0 || dot == segment.Length - 1) return false;

        string extension = segment[(dot + 1)..];
        return extension.Length is >= 2 and <= 8
               && extension.All(char.IsLetterOrDigit)
               && extension.Any(char.IsLetter);
    }

    /// <summary>
    /// Gives a name an extension from the server's content type when it has none
    /// — an endpoint like <c>/files/download?id=8821</c> otherwise saves a video
    /// as an extensionless "download" that nothing on the machine will open.
    /// A name that already has an extension is left alone: the server's idea of
    /// the type is not better than an explicit <c>.mkv</c>, and overriding it
    /// would rename every file served as the wrong MIME type.
    /// </summary>
    public static string EnsureExtension(string fileName, string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType)) return fileName;
        if (LooksLikeFileName(fileName)) return fileName;

        // "video/mp4; charset=binary" — the parameters are not part of the type.
        string mediaType = contentType.Split(';')[0].Trim();
        return ExtensionByContentType.TryGetValue(mediaType, out string? extension)
            ? $"{fileName.TrimEnd('.')}.{extension}"
            : fileName;
    }

    /// <summary>Appends " (1)", " (2)"... to avoid overwriting an existing file.</summary>
    public static string GetAvailableFilePath(string folder, string fileName)
    {
        string fullPath = Path.Combine(folder, fileName);
        if (!File.Exists(fullPath)) return fullPath;

        string nameOnly = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);

        for (int i = 1; ; i++)
        {
            string candidate = Path.Combine(folder, $"{nameOnly} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
