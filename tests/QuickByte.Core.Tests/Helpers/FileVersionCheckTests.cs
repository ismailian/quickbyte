using QuickByte.Core.Helpers;
using QuickByte.Core.Models;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// The verdict a resume acts on before it appends a byte.
///
/// Both wrong answers are expensive, in different currencies. "Unchanged" for a
/// file the publisher replaced merges two versions into one corrupt file, with
/// nothing anywhere reporting an error. "Changed" for a file that is fine throws
/// away hours of download. Most of these tests are about the second, because it
/// is the one a careful-looking rule gets wrong: mirror networks and rsync'd
/// servers hand the identical file out under validators that disagree.
/// </summary>
public sealed class FileVersionCheckTests
{
    private const string Host = "downloads.example.com";

    [Fact]
    public void A_different_size_is_a_different_file()
    {
        var verdict = FileVersionCheck.Compare(1000, Version("\"a\""), 1001, Version("\"a\""));

        Assert.Equal(FileVersionVerdict.Changed, verdict);
    }

    [Fact]
    public void A_different_size_counts_even_when_another_host_answered()
    {
        // The one signal that does not depend on who minted it. It is also what
        // catches the corruption the validators cannot: a length change shifts
        // every segment boundary, and the old chunks resume at the old offsets.
        var verdict = FileVersionCheck.Compare(
            1000, Version("\"a\"", host: "mirror1.example.org"),
            2000, Version("\"a\"", host: "mirror2.example.org"));

        Assert.Equal(FileVersionVerdict.Changed, verdict);
    }

    [Fact]
    public void The_same_etag_is_the_same_file()
    {
        Assert.Equal(FileVersionVerdict.Same,
            FileVersionCheck.Compare(1000, Version("\"a\""), 1000, Version("\"a\"")));
    }

    [Fact]
    public void A_different_etag_is_a_different_file()
    {
        Assert.Equal(FileVersionVerdict.Changed,
            FileVersionCheck.Compare(1000, Version("\"a\""), 1000, Version("\"b\"")));
    }

    [Fact]
    public void A_tag_a_proxy_weakened_still_names_the_same_file()
    {
        // nginx weakens the ETag of a response it compresses.
        Assert.Equal(FileVersionVerdict.Same,
            FileVersionCheck.Compare(1000, Version("\"a\""), 1000, Version("W/\"a\"")));
    }

    [Fact]
    public void Etags_that_disagree_with_dates_that_agree_decide_nothing()
    {
        // An rsync'd mirror keeps the file's timestamp, and an Apache that builds
        // its ETag from the inode number gives the same file a different tag.
        var verdict = FileVersionCheck.Compare(
            1000, Version("\"inode-1\"", Date(1)), 1000, Version("\"inode-2\"", Date(1)));

        Assert.Equal(FileVersionVerdict.Unknown, verdict);
    }

    [Fact]
    public void A_different_date_is_a_different_file_when_there_is_no_etag()
    {
        Assert.Equal(FileVersionVerdict.Changed,
            FileVersionCheck.Compare(1000, Version(null, Date(1)), 1000, Version(null, Date(2))));
    }

    [Fact]
    public void Validators_from_another_host_are_not_compared()
    {
        // A mirror redirector: the same file, from two servers that each minted
        // their own validator for it.
        var verdict = FileVersionCheck.Compare(
            1000, Version("\"a\"", Date(1), "mirror1.example.org"),
            1000, Version("\"b\"", Date(2), "mirror2.example.org"));

        Assert.Equal(FileVersionVerdict.Unknown, verdict);
    }

    [Fact]
    public void Hosts_are_compared_without_regard_to_case()
    {
        Assert.Equal(FileVersionVerdict.Changed,
            FileVersionCheck.Compare(
                1000, Version("\"a\"", host: "Downloads.Example.com"),
                1000, Version("\"b\"", host: "downloads.example.com")));
    }

    [Fact]
    public void A_download_recorded_before_versions_were_kept_is_judged_on_size_alone()
    {
        Assert.Equal(FileVersionVerdict.Unknown, FileVersionCheck.Compare(1000, null, 1000, Version("\"a\"")));
        Assert.Equal(FileVersionVerdict.Changed, FileVersionCheck.Compare(1000, null, 1200, Version("\"a\"")));
    }

    [Fact]
    public void An_unknown_size_on_either_side_is_not_a_size_change()
    {
        Assert.Equal(FileVersionVerdict.Same, FileVersionCheck.Compare(0, Version("\"a\""), 1000, Version("\"a\"")));
    }

    [Fact]
    public void Nothing_to_compare_is_not_evidence_of_a_change()
    {
        Assert.Equal(FileVersionVerdict.Unknown,
            FileVersionCheck.Compare(1000, Version(null), 1000, Version(null)));
    }

    private static RemoteFileVersion Version(string? etag, DateTimeOffset? lastModified = null, string host = Host) =>
        new() { ETag = etag, LastModified = lastModified, Host = host };

    private static DateTimeOffset Date(int day) => new(2026, 9, day, 12, 0, 0, TimeSpan.Zero);
}
