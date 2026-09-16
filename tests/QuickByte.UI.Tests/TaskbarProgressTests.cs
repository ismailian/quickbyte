using QuickByte.Core.Enums;
using QuickByte.UI.Controls;

namespace QuickByte.UI.Tests;

/// <summary>
/// The download-status-to-taskbar-indicator mapping behind the details window's
/// taskbar button.
///
/// The rest of <see cref="TaskbarProgress"/> is the shell talking to a window
/// handle and cannot be tested without one, but this is where the bugs with no
/// symptom live: nothing anywhere fails when a finished download leaves its
/// button tinted red, or when a paused one keeps a marquee running as though it
/// were still transferring. The only way to notice is to look at the taskbar.
/// </summary>
public sealed class TaskbarProgressTests
{
    [Theory]
    [InlineData(DownloadStatus.Downloading)]
    [InlineData(DownloadStatus.Connecting)]
    [InlineData(DownloadStatus.Merging)]
    public void A_download_in_flight_shows_a_bar(DownloadStatus status)
    {
        Assert.Equal(TaskbarProgressState.Normal, TaskbarProgress.StateFor(status, sizeKnown: true, percentage: 42));
    }

    [Fact]
    public void A_merge_keeps_the_bar_full()
    {
        // The bytes are all on disk by then — the window's own bar stays full
        // and reports the merge as text, and the taskbar has nowhere to put
        // text, so it just stays full too.
        Assert.Equal(TaskbarProgressState.Normal, TaskbarProgress.StateFor(DownloadStatus.Merging, sizeKnown: true, percentage: 100));
    }

    [Fact]
    public void A_download_of_unknown_size_gets_the_marquee()
    {
        // Percentage is permanently zero for these, so a determinate bar would
        // sit empty at the left edge for the whole download.
        Assert.Equal(TaskbarProgressState.Indeterminate, TaskbarProgress.StateFor(DownloadStatus.Downloading, sizeKnown: false, percentage: 0));
    }

    [Fact]
    public void Connecting_with_nothing_downloaded_yet_gets_the_marquee()
    {
        Assert.Equal(TaskbarProgressState.Indeterminate, TaskbarProgress.StateFor(DownloadStatus.Connecting, sizeKnown: true, percentage: 0));
    }

    [Fact]
    public void Connecting_to_resume_keeps_the_progress_already_made()
    {
        // A resume reconnects with most of the file already on disk. Dropping to
        // a marquee there would throw away the one number the user wants.
        Assert.Equal(TaskbarProgressState.Normal, TaskbarProgress.StateFor(DownloadStatus.Connecting, sizeKnown: true, percentage: 80));
    }

    [Fact]
    public void A_paused_download_turns_the_bar_yellow()
    {
        Assert.Equal(TaskbarProgressState.Paused, TaskbarProgress.StateFor(DownloadStatus.Paused, sizeKnown: true, percentage: 30));
    }

    [Fact]
    public void A_paused_download_of_unknown_size_still_reads_as_paused()
    {
        // Not the marquee: a moving indicator on something that has stopped is
        // worse than an empty one.
        Assert.Equal(TaskbarProgressState.Paused, TaskbarProgress.StateFor(DownloadStatus.Paused, sizeKnown: false, percentage: 0));
    }

    [Fact]
    public void A_failed_download_turns_the_bar_red()
    {
        Assert.Equal(TaskbarProgressState.Error, TaskbarProgress.StateFor(DownloadStatus.Failed, sizeKnown: true, percentage: 30));
    }

    [Theory]
    [InlineData(DownloadStatus.Completed)]
    [InlineData(DownloadStatus.Cancelled)]
    [InlineData(DownloadStatus.Queued)]
    public void A_download_that_is_not_running_leaves_the_button_alone(DownloadStatus status)
    {
        // Including Completed at 100%: an indicator nothing ever clears is the
        // failure this mapping exists to avoid.
        Assert.Equal(TaskbarProgressState.None, TaskbarProgress.StateFor(status, sizeKnown: true, percentage: 100));
    }
}
