using QuickByte.Core.Helpers;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// Whether a download will fit, decided before it starts. A download that runs
/// out of disk used to find out at 100%, in the merge — the most expensive
/// moment there is.
/// </summary>
public sealed class DiskSpaceTests
{
    private const long GB = 1024L * 1024 * 1024;

    [Fact]
    public void Paths_on_one_drive_share_a_volume_whatever_their_case()
    {
        Assert.True(DiskSpace.SameVolume(@"C:\Users\a\AppData\QuickByte\temp", @"c:\Downloads"));
        Assert.False(DiskSpace.SameVolume(@"C:\temp", @"D:\Downloads"));
    }

    [Fact]
    public void A_share_is_never_assumed_to_be_the_same_volume_as_anything()
    {
        // Renaming across it is not possible; copying always is.
        Assert.False(DiskSpace.SameVolume(@"\\server\share\temp", @"\\server\share\downloads"));
        Assert.Null(DiskSpace.VolumeOf(@"\\server\share\file.bin"));
    }

    [Fact]
    public void The_real_system_drive_reports_some_free_space()
    {
        Assert.True(DiskSpace.AvailableBytes(Path.GetTempPath()) > 0);
    }

    [Fact]
    public void A_download_on_one_drive_needs_the_rest_of_the_file_plus_one_chunk_for_the_merge()
    {
        var needs = DiskSpace.StillNeeded(@"C:\temp", @"C:\Downloads", totalBytes: 40 * GB, downloadedBytes: 10 * GB,
            largestChunk: 5 * GB, chunkCount: 8).ToList();

        Assert.Equal(new[] { (@"C:\temp", 30 * GB), (@"C:\Downloads", 5 * GB) }, needs);
    }

    [Fact]
    public void A_single_chunk_on_one_drive_is_renamed_and_needs_nothing_more()
    {
        var needs = DiskSpace.StillNeeded(@"C:\temp", @"C:\Downloads", 40 * GB, 0, 40 * GB, chunkCount: 1).ToList();

        Assert.Equal(0, needs.Single(n => n.Path == @"C:\Downloads").Bytes);
    }

    [Fact]
    public void A_download_to_another_drive_needs_the_whole_file_there()
    {
        var needs = DiskSpace.StillNeeded(@"C:\temp", @"D:\Downloads", 40 * GB, 10 * GB, 5 * GB, 8).ToList();

        Assert.Equal(40 * GB, needs.Single(n => n.Path == @"D:\Downloads").Bytes);
    }

    [Fact]
    public void A_file_of_unknown_size_asks_for_nothing()
    {
        Assert.Empty(DiskSpace.StillNeeded(@"C:\temp", @"C:\Downloads", 0, 0, 0, 1));
    }

    [Fact]
    public void Needs_on_the_same_drive_are_added_together()
    {
        // 30 GB of chunks and 5 GB for the merge do not each fit in 32 GB free —
        // they have to fit together.
        var shortfall = DiskSpace.FindShortfall(
            new[] { (@"C:\temp", 30 * GB), (@"C:\Downloads", 5 * GB) },
            _ => 32 * GB);

        Assert.Equal(new SpaceShortfall(@"C:\", 35 * GB, 32 * GB), shortfall);
    }

    [Fact]
    public void Needs_that_fit_report_no_shortfall()
    {
        Assert.Null(DiskSpace.FindShortfall(new[] { (@"C:\temp", 30 * GB), (@"D:\Downloads", 40 * GB) },
            path => path.StartsWith("C") ? 31 * GB : 41 * GB));
    }

    [Fact]
    public void A_drive_that_cannot_be_measured_does_not_stop_a_download()
    {
        Assert.Null(DiskSpace.FindShortfall(new[] { (@"C:\temp", 30 * GB) }, _ => null));
    }
}
