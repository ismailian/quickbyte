using QuickByte.Core.Enums;
using QuickByte.Core.Helpers;
using QuickByte.Core.Models;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// What happens when something finishes. The cases that matter are the ones that
/// must <em>not</em> turn a computer off: a queue that was stopped rather than
/// emptied, a download that did not actually complete, a value in a hand-edited
/// file that names no action at all.
/// </summary>
public sealed class CompletionPlanTests
{
    [Fact]
    public void A_completed_download_asks_for_what_it_was_set_to()
    {
        var item = Completed(CompletionActions.OpenFolder | CompletionActions.PlaySound, PowerAction.ShutDown);

        var plan = CompletionPlan.ForDownload(item, new[] { item });

        Assert.True(plan.RevealFolder);
        Assert.True(plan.PlaySound);
        Assert.Equal(PowerAction.ShutDown, plan.Power);
        Assert.Equal(0, plan.StillRunning);
    }

    [Fact]
    public void A_download_that_did_not_complete_asks_for_nothing()
    {
        var item = Completed(CompletionActions.OpenFolder, PowerAction.ShutDown);
        item.Status = DownloadStatus.Failed;

        Assert.Same(CompletionPlan.Nothing, CompletionPlan.ForDownload(item, new[] { item }));
    }

    [Fact]
    public void The_plan_counts_the_other_downloads_the_action_would_interrupt()
    {
        var done = Completed(CompletionActions.None, PowerAction.Sleep);
        var others = new[]
        {
            new DownloadItem { Status = DownloadStatus.Downloading },
            new DownloadItem { Status = DownloadStatus.Merging },
            new DownloadItem { Status = DownloadStatus.Paused },
            new DownloadItem { Status = DownloadStatus.Completed }
        };

        var plan = CompletionPlan.ForDownload(done, others.Append(done));

        Assert.Equal(2, plan.StillRunning);
    }

    [Fact]
    public void A_power_action_no_version_of_the_app_knows_is_no_action()
    {
        var item = Completed(CompletionActions.None, (PowerAction)42);

        Assert.Equal(PowerAction.None, CompletionPlan.ForDownload(item, new[] { item }).Power);
        Assert.False(CompletionPlan.ForDownload(item, new[] { item }).HasPowerAction);
    }

    [Fact]
    public void A_queue_that_worked_through_every_file_does_what_it_was_set_to()
    {
        var queue = new DownloadQueue { WhenFinished = PowerAction.Hibernate };

        var plan = CompletionPlan.ForQueue(queue, drained: true, Array.Empty<DownloadItem>());

        Assert.Equal(PowerAction.Hibernate, plan.Power);
    }

    [Fact]
    public void A_queue_stopped_before_it_was_done_never_shuts_anything_down()
    {
        // Stopped by the user, or cut off by its own stop time with files left:
        // turning the machine off on top of that is the opposite of what was asked.
        var queue = new DownloadQueue { WhenFinished = PowerAction.ShutDown };

        Assert.False(CompletionPlan.ForQueue(queue, drained: false, Array.Empty<DownloadItem>()).HasPowerAction);
    }

    private static DownloadItem Completed(CompletionActions actions, PowerAction power) => new()
    {
        Status = DownloadStatus.Completed,
        OnCompletion = actions,
        PowerActionWhenDone = power
    };
}
