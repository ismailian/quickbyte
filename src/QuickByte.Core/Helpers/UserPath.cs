namespace QuickByte.Core.Helpers;

/// <summary>
/// Turns a folder under the user's own profile into the short <c>~\…</c> form
/// for display, and back again for use.
///
/// It exists for one field: Add Download's "Save to". That box is nearly always
/// showing a folder inside the current user's profile, and
/// <c>C:\Users\somebody\Downloads\QuickByte</c> is both wider than the box and
/// almost entirely made of characters that tell the reader nothing —
/// the interesting part is the tail, and the tail is what gets clipped.
/// <c>~\Downloads\QuickByte</c> fits, and says the same thing.
///
/// A path outside the profile is left exactly as it is. That is the point of
/// the abbreviation rather than a limitation of it: <c>D:\Media</c> or
/// <c>\\nas\share</c> is somewhere the user deliberately chose, and the whole
/// path is the information.
///
/// <see cref="Expand"/> is not optional — the shortened text is shown in an
/// editable box whose value becomes <see cref="Models.DownloadRequest.SaveFolder"/>,
/// and a literal <c>~</c> reaching <c>Directory.CreateDirectory</c> creates a
/// folder named <c>~</c> next to the executable. Every read of that box goes
/// through here.
/// </summary>
public static class UserPath
{
    private const char Tilde = '~';

    /// <summary>
    /// The profile folder, resolved once. <c>Environment.SpecialFolder.UserProfile</c>
    /// can come back empty on a stripped-down or service account, and an empty
    /// prefix would match every path — so an empty answer disables the
    /// abbreviation entirely rather than turning every path into <c>~</c>.
    /// </summary>
    private static string Profile { get; } = Path.TrimEndingDirectorySeparator(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// <paramref name="path"/> with the user's profile folder replaced by
    /// <c>~</c>, or unchanged when it is somewhere else.
    /// </summary>
    public static string Shorten(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(Profile)) return path ?? string.Empty;

        string trimmed = Path.TrimEndingDirectorySeparator(path.Trim());

        if (trimmed.Equals(Profile, StringComparison.OrdinalIgnoreCase))
            return Tilde.ToString();

        // The separator is part of the comparison, or C:\Users\bobby matches a
        // path under C:\Users\bob and is abbreviated into a folder that is not
        // the one it names.
        if (trimmed.Length > Profile.Length
            && trimmed.StartsWith(Profile, StringComparison.OrdinalIgnoreCase)
            && IsSeparator(trimmed[Profile.Length]))
        {
            return Tilde + trimmed[Profile.Length..];
        }

        return trimmed;
    }

    /// <summary>
    /// The real path behind a possibly-shortened one. Anything that does not
    /// start with <c>~</c> at a folder boundary is returned untouched — including
    /// a folder whose name genuinely begins with a tilde.
    /// </summary>
    public static string Expand(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text ?? string.Empty;

        string trimmed = text.Trim();
        if (trimmed[0] != Tilde || string.IsNullOrEmpty(Profile)) return trimmed;

        if (trimmed.Length == 1) return Profile;
        if (!IsSeparator(trimmed[1])) return trimmed;

        // Separators normalised on the way out: people type "~/Downloads", and
        // the expanded path is persisted on the DownloadItem and shown in the
        // details window. A path with both separators in it works, and looks
        // like a bug.
        return Profile + Normalize(trimmed[1..]);
    }

    private static string Normalize(string path) =>
        Path.AltDirectorySeparatorChar == Path.DirectorySeparatorChar
            ? path
            : path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

    private static bool IsSeparator(char c) =>
        c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
}
