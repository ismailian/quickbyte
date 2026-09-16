using QuickByte.Core.Enums;
using QuickByte.Core.Helpers;
using QuickByte.Core.Models;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// Whether a new address may carry on a download that already has bytes on disk.
///
/// A refreshed link that is wrongly accepted corrupts the file, silently; one
/// that is wrongly refused sends the user back to square one on a download that
/// was fine. The common case — a signed link that simply expired — has to sail
/// through, and so does switching to another mirror of the same file.
/// </summary>
public sealed class AddressRefreshTests
{
    private const string Host = "downloads.example.com";

    [Fact]
    public void An_expired_link_renewed_for_the_same_file_carries_on()
    {
        var item = Paused(total: 1000, downloaded: 400, etag: "\"v1\"");

        Assert.Equal(AddressVerdict.SameFile, AddressRefresh.Judge(item, Probe(1000, "\"v1\"")));
    }

    [Fact]
    public void A_link_to_a_file_of_another_size_is_refused_whatever_the_progress()
    {
        Assert.Equal(AddressVerdict.DifferentSize,
            AddressRefresh.Judge(Paused(1000, 400, "\"v1\""), Probe(2000, "\"v1\"")));
        Assert.Equal(AddressVerdict.DifferentSize,
            AddressRefresh.Judge(Paused(1000, 0, "\"v1\""), Probe(2000, "\"v1\"")));
    }

    [Fact]
    public void A_new_version_from_the_same_server_can_only_start_over()
    {
        var item = Paused(1000, 400, "\"v1\"");

        Assert.Equal(AddressVerdict.NewVersion, AddressRefresh.Judge(item, Probe(1000, "\"v2\"")));
    }

    [Fact]
    public void A_new_version_costs_nothing_when_nothing_was_downloaded()
    {
        Assert.Equal(AddressVerdict.SameFile, AddressRefresh.Judge(Paused(1000, 0, "\"v1\""), Probe(1000, "\"v2\"")));
    }

    [Fact]
    public void Another_mirror_of_the_same_file_carries_on_despite_its_own_etag()
    {
        // The ETag of a different host is not evidence of anything.
        var item = Paused(1000, 400, "\"inode-1\"");

        var verdict = AddressRefresh.Judge(item, Probe(1000, "\"inode-2\"", host: "mirror2.example.org"));

        Assert.Equal(AddressVerdict.SameFile, verdict);
    }

    [Fact]
    public void A_size_nobody_can_compare_is_the_user_s_call_only_when_there_is_progress_to_lose()
    {
        Assert.Equal(AddressVerdict.Unverifiable, AddressRefresh.Judge(Paused(1000, 400, null), Probe(0, null)));
        Assert.Equal(AddressVerdict.Unverifiable, AddressRefresh.Judge(Paused(0, 400, null), Probe(1000, null)));
        Assert.Equal(AddressVerdict.SameFile, AddressRefresh.Judge(Paused(0, 0, null), Probe(1000, null)));
    }

    [Fact]
    public void A_cancelled_download_has_no_progress_whatever_its_counter_says()
    {
        // Stop deleted the chunks; the counter is just what it last showed.
        var item = Paused(1000, 400, "\"v1\"");
        item.Status = DownloadStatus.Cancelled;

        Assert.False(AddressRefresh.HasProgress(item));
        Assert.Equal(AddressVerdict.SameFile, AddressRefresh.Judge(item, Probe(1000, "\"v2\"")));
    }

    // ------------------------------------------------- browser captures --

    [Fact]
    public void A_capture_of_the_same_size_is_the_link_a_refresh_is_waiting_for()
    {
        var item = Paused(1000, 400, null);

        Assert.True(AddressRefresh.MatchesCapture(item, new CapturedDownload { Url = "https://x/y", TotalBytes = 1000 }));
        Assert.False(AddressRefresh.MatchesCapture(item, new CapturedDownload { Url = "https://x/y", TotalBytes = 999 }));
    }

    [Fact]
    public void Without_a_size_on_either_side_the_file_name_decides()
    {
        var item = Paused(0, 400, null);

        Assert.True(AddressRefresh.MatchesCapture(item, new CapturedDownload { FileName = "SETUP.EXE", TotalBytes = 1000 }));
        Assert.False(AddressRefresh.MatchesCapture(item, new CapturedDownload { FileName = "other.exe", TotalBytes = 1000 }));
        Assert.False(AddressRefresh.MatchesCapture(item, new CapturedDownload { FileName = null }));
    }

    [Fact]
    public void A_different_size_is_not_rescued_by_a_matching_name()
    {
        // A capture wrongly claimed is a download the user asked for that never
        // appears, so a size that disagrees is final.
        var item = Paused(1000, 400, null);

        Assert.False(AddressRefresh.MatchesCapture(item, new CapturedDownload { FileName = "setup.exe", TotalBytes = 5 }));
    }

    [Fact]
    public void A_running_download_is_not_waiting_for_anything()
    {
        var item = Paused(1000, 400, null);
        item.Status = DownloadStatus.Downloading;

        Assert.False(AddressRefresh.MatchesCapture(item, new CapturedDownload { TotalBytes = 1000 }));
    }

    private static DownloadItem Paused(long total, long downloaded, string? etag) => new()
    {
        FileName = "setup.exe",
        Status = DownloadStatus.Paused,
        TotalBytes = total,
        DownloadedBytes = downloaded,
        RemoteVersion = etag is null ? null : new RemoteFileVersion { ETag = etag, Host = Host }
    };

    private static RemoteFileInfo Probe(long size, string? etag, string host = Host) => new()
    {
        ContentLength = size,
        ETag = etag,
        FinalUrl = $"https://{host}/setup.exe"
    };
}
