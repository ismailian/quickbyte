using QuickByte.Core.Models;

namespace QuickByte.Core.Helpers;

public enum FileVersionVerdict
{
    /// <summary>Nothing comparable, or evidence pointing both ways. Keep what is on disk.</summary>
    Unknown,

    /// <summary>A validator both sides share agrees, and nothing contradicts it.</summary>
    Same,

    /// <summary>The server is describing a different file from the one the chunks came from.</summary>
    Changed
}

/// <summary>
/// Decides whether two descriptions of a remote file describe the same version
/// of it — the question a resume has to answer before it appends anything.
///
/// The two ways to be wrong are not symmetrical, and the rules lean accordingly.
/// Calling an unchanged file changed throws away progress that was fine, which
/// on a 30 GB download is hours. Calling a changed file unchanged corrupts it,
/// but that is exactly what every resume did before this existed. So a verdict of
/// <see cref="FileVersionVerdict.Changed"/> needs evidence that does not argue
/// with itself:
/// <list type="bullet">
/// <item>A different size is decisive. It is also the one signal that does not
/// depend on which server answered, so it is checked across hosts.</item>
/// <item>A different ETag counts only when the Last-Modified dates do not agree.
/// Mirrors synchronised with rsync keep the file's timestamp but can compute
/// their ETags from something local, such as an inode number.</item>
/// <item>Validators from two different hosts are not compared at all — see
/// <see cref="RemoteFileVersion.Host"/>.</item>
/// </list>
/// </summary>
public static class FileVersionCheck
{
    public static FileVersionVerdict Compare(
        long knownSize, RemoteFileVersion? known, long probedSize, RemoteFileVersion? probed)
    {
        if (knownSize > 0 && probedSize > 0 && knownSize != probedSize)
            return FileVersionVerdict.Changed;

        if (known is null || probed is null || !SameHost(known.Host, probed.Host))
            return FileVersionVerdict.Unknown;

        bool? etagsAgree = known.ETag is not null && probed.ETag is not null
            ? SameOpaqueTag(known.ETag, probed.ETag)
            : null;

        bool? datesAgree = known.LastModified is { } a && probed.LastModified is { } b
            ? a == b
            : null;

        return (etagsAgree, datesAgree) switch
        {
            (true, false) or (false, true) => FileVersionVerdict.Unknown,
            (false, _) or (_, false) => FileVersionVerdict.Changed,
            (true, _) or (_, true) => FileVersionVerdict.Same,
            _ => FileVersionVerdict.Unknown
        };
    }

    /// <summary>
    /// Compares the quoted part of two entity tags, ignoring weakness. A proxy
    /// that compresses a response is allowed to weaken its tag (nginx does), and
    /// that does not make it a different file.
    /// </summary>
    public static bool SameOpaqueTag(string a, string b) =>
        string.Equals(StripWeakness(a), StripWeakness(b), StringComparison.Ordinal);

    /// <summary>
    /// A missing host on either side is treated as a match: it only happens for
    /// an address that did not parse, and refusing to compare would disable the
    /// check for no reason anyone could see.
    /// </summary>
    public static bool SameHost(string? a, string? b) =>
        a is null || b is null || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string StripWeakness(string tag) =>
        tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;
}
