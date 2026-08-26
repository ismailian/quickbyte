using QuickByte.Core.Enums;
using QuickByte.Core.Helpers;
using QuickByte.Core.Models;

namespace QuickByte.Core.Tests.Helpers;

public sealed class SpeedCalculatorTests
{
    [Fact]
    public void A_single_sample_is_not_a_speed() =>
        Assert.Equal(0, new SpeedCalculator().GetSpeedBytesPerSecond());

    [Fact]
    public void One_sample_alone_yields_nothing_to_divide()
    {
        var calculator = new SpeedCalculator();
        calculator.AddSample(1000);

        Assert.Equal(0, calculator.GetSpeedBytesPerSecond());
    }

    [Fact]
    public async Task Two_samples_give_a_positive_rate()
    {
        var calculator = new SpeedCalculator();
        calculator.AddSample(0);
        await Task.Delay(60);
        calculator.AddSample(60_000);

        // Rate over a ~60 ms window; assert the direction rather than a figure
        // that depends on timer resolution.
        Assert.True(calculator.GetSpeedBytesPerSecond() > 0);
    }

    [Fact]
    public async Task A_download_that_stopped_moving_reports_no_speed()
    {
        var calculator = new SpeedCalculator();
        calculator.AddSample(5000);
        await Task.Delay(30);
        calculator.AddSample(5000);

        Assert.Equal(0, calculator.GetSpeedBytesPerSecond());
    }

    [Fact]
    public async Task Samples_older_than_the_window_are_dropped()
    {
        var calculator = new SpeedCalculator(TimeSpan.FromMilliseconds(50));
        calculator.AddSample(0);

        await Task.Delay(120);

        // The old sample falls out of the window as this one arrives, leaving one
        // sample and therefore no rate — rather than averaging over the gap.
        calculator.AddSample(1_000_000);

        Assert.Equal(0, calculator.GetSpeedBytesPerSecond());
    }

    [Theory]
    [InlineData(0, 1000, 100)]
    [InlineData(500, 1000, 0)]
    [InlineData(500, 1000, -1)]
    [InlineData(500, 0, 100)]
    [InlineData(500, -1, 100)]
    public void EstimateTimeRemaining_declines_to_guess_without_both_numbers(
        long downloaded, long total, double speed)
    {
        var eta = SpeedCalculator.EstimateTimeRemaining(downloaded, total, speed);

        if (speed <= 0.01 || total <= 0) Assert.Null(eta);
        else Assert.NotNull(eta);
    }

    [Fact]
    public void EstimateTimeRemaining_divides_what_is_left_by_the_rate()
    {
        var eta = SpeedCalculator.EstimateTimeRemaining(downloaded: 500, total: 1500, speedBytesPerSecond: 100);

        Assert.Equal(TimeSpan.FromSeconds(10), eta);
    }

    [Fact]
    public void EstimateTimeRemaining_of_an_over_counted_download_is_zero()
    {
        // DownloadedBytes is pinned to TotalBytes during the merge, and a stray
        // sample can overshoot; a negative remainder must not become a negative ETA.
        Assert.Equal(TimeSpan.Zero, SpeedCalculator.EstimateTimeRemaining(2000, 1000, 100));
    }

    [Fact]
    public void EstimateTimeRemaining_refuses_an_answer_it_cannot_represent()
    {
        // A byte a millennium on a very large file overflows TimeSpan; "--" is a
        // better answer than an exception on the progress-report path.
        Assert.Null(SpeedCalculator.EstimateTimeRemaining(0, long.MaxValue, 0.02));
    }
}

/// <summary>
/// One set of predicates decides whether a toolbar button is greyed, whether a
/// row's context-menu entry appears at all, and whether a tray command has
/// anything to act on. The menus used to offer Resume on a download that was
/// already running because there were three copies of these rules.
/// </summary>
public sealed class DownloadActionsTests
{
    private static DownloadItem With(DownloadStatus status) => new() { Status = status };

    [Theory]
    [InlineData(DownloadStatus.Queued, true)]
    [InlineData(DownloadStatus.Paused, true)]
    [InlineData(DownloadStatus.Failed, true)]
    [InlineData(DownloadStatus.Cancelled, true)]
    [InlineData(DownloadStatus.Connecting, false)]
    [InlineData(DownloadStatus.Downloading, false)]
    [InlineData(DownloadStatus.Merging, false)]
    [InlineData(DownloadStatus.Completed, false)]
    public void CanResume_covers_everything_not_in_flight_or_finished(DownloadStatus status, bool expected) =>
        Assert.Equal(expected, DownloadActions.CanResume(With(status)));

    [Theory]
    [InlineData(DownloadStatus.Connecting, true)]
    [InlineData(DownloadStatus.Downloading, true)]
    [InlineData(DownloadStatus.Merging, false)]
    [InlineData(DownloadStatus.Queued, false)]
    [InlineData(DownloadStatus.Paused, false)]
    [InlineData(DownloadStatus.Completed, false)]
    [InlineData(DownloadStatus.Failed, false)]
    [InlineData(DownloadStatus.Cancelled, false)]
    public void CanPause_excludes_merging_which_has_no_resumable_midpoint(DownloadStatus status, bool expected) =>
        Assert.Equal(expected, DownloadActions.CanPause(With(status)));

    [Theory]
    [InlineData(DownloadStatus.Queued, true)]
    [InlineData(DownloadStatus.Connecting, true)]
    [InlineData(DownloadStatus.Downloading, true)]
    [InlineData(DownloadStatus.Paused, true)]
    [InlineData(DownloadStatus.Merging, false)]
    [InlineData(DownloadStatus.Completed, false)]
    [InlineData(DownloadStatus.Failed, false)]
    [InlineData(DownloadStatus.Cancelled, false)]
    public void CanStop_is_offered_for_anything_unfinished(DownloadStatus status, bool expected)
    {
        // Including a paused download, where Stop is the discard half of the
        // pause/discard distinction.
        Assert.Equal(expected, DownloadActions.CanStop(With(status)));
    }

    [Theory]
    [InlineData(DownloadStatus.Failed, true)]
    [InlineData(DownloadStatus.Cancelled, true)]
    [InlineData(DownloadStatus.Paused, false)]
    [InlineData(DownloadStatus.Completed, false)]
    [InlineData(DownloadStatus.Downloading, false)]
    public void CanRetry_applies_only_once_a_download_has_ended_badly(DownloadStatus status, bool expected) =>
        Assert.Equal(expected, DownloadActions.CanRetry(With(status)));

    [Theory]
    [InlineData(DownloadStatus.Connecting, true)]
    [InlineData(DownloadStatus.Downloading, true)]
    [InlineData(DownloadStatus.Merging, true)]
    [InlineData(DownloadStatus.Queued, false)]
    [InlineData(DownloadStatus.Paused, false)]
    [InlineData(DownloadStatus.Completed, false)]
    public void IsActive_is_what_the_status_bar_and_tray_tooltip_count(DownloadStatus status, bool expected) =>
        Assert.Equal(expected, DownloadActions.IsActive(With(status)));

    [Fact]
    public void CanOpenFile_wants_a_completed_download_whose_file_is_there()
    {
        using var folder = new TempFolder();
        folder.WriteFile("done.bin", new byte[] { 1 });

        var present = new DownloadItem
        {
            Status = DownloadStatus.Completed, SaveFolder = folder.Path, FileName = "done.bin"
        };
        var deletedByHand = new DownloadItem
        {
            Status = DownloadStatus.Completed, SaveFolder = folder.Path, FileName = "gone.bin"
        };
        var stillRunning = new DownloadItem
        {
            Status = DownloadStatus.Downloading, SaveFolder = folder.Path, FileName = "done.bin"
        };

        Assert.True(DownloadActions.CanOpenFile(present));
        Assert.False(DownloadActions.CanOpenFile(deletedByHand));
        Assert.False(DownloadActions.CanOpenFile(stillRunning));
    }

    [Fact]
    public void CanOpenFolder_only_needs_the_folder_to_exist()
    {
        using var folder = new TempFolder();

        // True at any status — the folder is worth opening for a failed download too.
        Assert.True(DownloadActions.CanOpenFolder(
            new DownloadItem { Status = DownloadStatus.Failed, SaveFolder = folder.Path }));
        Assert.False(DownloadActions.CanOpenFolder(
            new DownloadItem { SaveFolder = Path.Combine(folder.Path, "nope") }));
        Assert.False(DownloadActions.CanOpenFolder(new DownloadItem { SaveFolder = "   " }));
    }
}
