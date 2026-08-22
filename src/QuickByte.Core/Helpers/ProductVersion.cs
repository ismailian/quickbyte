namespace QuickByte.Core.Helpers;

/// <summary>
/// Compares the product-version strings QuickByte actually deals with:
/// "1.3.0" off the running assembly, and whatever the release manifest was
/// hand-written with ("v1.3.0", "1.3.0-beta.1", "1.3").
///
/// <see cref="System.Version"/> alone can't do this — it rejects a "v" prefix
/// and a pre-release suffix outright, and it has no notion that "1.3.0-beta.1"
/// precedes "1.3.0". Getting that last part wrong is what would offer a beta
/// user an "update" to the build they are already running.
/// </summary>
public static class ProductVersion
{
    /// <summary>
    /// True when <paramref name="candidate"/> is a strictly newer release than
    /// <paramref name="current"/>. Anything unparseable answers false: an
    /// update prompt is worth suppressing when the input is nonsense, and the
    /// alternative is prompting on every launch forever.
    /// </summary>
    public static bool IsNewer(string? candidate, string? current)
    {
        if (!TryParse(candidate, out var candidateNumber, out string candidatePre)) return false;
        if (!TryParse(current, out var currentNumber, out string currentPre)) return false;

        int byNumber = candidateNumber.CompareTo(currentNumber);
        if (byNumber != 0) return byNumber > 0;

        // Same numbers: a release supersedes a pre-release of itself ("1.3.0"
        // over "1.3.0-beta.1"), and nothing supersedes a release.
        return currentPre.Length > 0 && candidatePre.Length == 0;
    }

    /// <summary>
    /// Splits a product version into its numeric part and its pre-release
    /// label, normalizing the two things the numeric parser won't take: a
    /// leading "v", and a "-suffix"/"+metadata" tail.
    /// </summary>
    public static bool TryParse(string? text, out Version version, out string prerelease)
    {
        version = new Version(0, 0);
        prerelease = string.Empty;

        if (string.IsNullOrWhiteSpace(text)) return false;

        string trimmed = text.Trim();
        if (trimmed.Length > 1 && (trimmed[0] == 'v' || trimmed[0] == 'V')) trimmed = trimmed[1..];

        // Build metadata never affects ordering, so it is dropped before the
        // pre-release label is read off.
        int plus = trimmed.IndexOf('+');
        if (plus >= 0) trimmed = trimmed[..plus];

        int dash = trimmed.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = trimmed[(dash + 1)..];
            trimmed = trimmed[..dash];
        }

        // Version.TryParse wants at least two components; "1" is a legal thing
        // for someone to write in a manifest.
        if (!trimmed.Contains('.')) trimmed += ".0";

        if (!Version.TryParse(trimmed, out var parsed)) return false;

        // Version treats an omitted component as -1, which would order "1.3"
        // *before* "1.3.0". Padding the missing parts out to zero makes the two
        // spellings of the same release compare equal, which is what a person
        // writing either one into a manifest means.
        version = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        return true;
    }
}
