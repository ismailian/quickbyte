namespace QuickByte.Core.Helpers;

/// <summary>
/// Decides whether something copied to the clipboard is a download worth
/// offering — the rule behind Options &gt; Interface &gt; "Watch the clipboard".
///
/// The clipboard carries everything a user copies: passwords, messages, whole
/// documents. So the rule is deliberately narrow. Only a clipboard that holds
/// <em>one</em> link and nothing else counts, only to a file whose extension is on
/// the user's list, and nothing about any other clipboard content is looked at,
/// kept or shown.
/// </summary>
public static class ClipboardLinks
{
    /// <summary>What a download manager is usually wanted for, and not what a browser shows inline.</summary>
    public const string DefaultFileTypes =
        "zip rar 7z gz xz bz2 tar iso img dmg exe msi msix appx apk deb rpm mp4 mkv avi mov webm mp3 flac wav m4a pdf epub";

    /// <summary>
    /// Longer than any real download link, and short enough that a pasted document
    /// is never scanned.
    /// </summary>
    public const int MaxLinkLength = 2048;

    /// <summary>
    /// The extensions in a user-edited list — separated by spaces, commas or
    /// semicolons, with or without dots, in any case. Anything that is not a plain
    /// extension is dropped rather than guessed at.
    /// </summary>
    public static IReadOnlySet<string> ParseFileTypes(string? list)
    {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in (list ?? string.Empty).Split(new[] { ' ', ',', ';', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string type = part.Trim().TrimStart('*').TrimStart('.');
            if (type.Length is > 0 and <= 10 && type.All(char.IsAsciiLetterOrDigit))
                types.Add(type.ToLowerInvariant());
        }
        return types;
    }

    /// <summary>
    /// The link to offer, or null. <paramref name="text"/> must be a single
    /// http(s) or ftp(s) URL — surrounding whitespace aside — whose path ends in
    /// one of <paramref name="fileTypes"/>.
    /// </summary>
    public static string? FindDownloadLink(string? text, IReadOnlySet<string> fileTypes)
    {
        if (string.IsNullOrWhiteSpace(text) || fileTypes.Count == 0) return null;

        string candidate = text.Trim();
        if (candidate.Length > MaxLinkLength || candidate.Any(char.IsWhiteSpace)) return null;

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https" or "ftp" or "ftps") || string.IsNullOrEmpty(uri.Host)) return null;

        string extension = Path.GetExtension(Uri.UnescapeDataString(uri.AbsolutePath)).TrimStart('.');
        return extension.Length > 0 && fileTypes.Contains(extension) ? candidate : null;
    }
}
