using QuickByte.Core.Enums;
using QuickByte.Core.Models;

namespace QuickByte.Core.Helpers;

/// <summary>
/// What should happen now that something finished: the per-download touches, and
/// the power action with what it would interrupt.
///
/// Kept apart from the window that acts on it so the decision can be checked
/// without shutting a test machine down. The interesting part is
/// <see cref="StillRunning"/>: a "shut down when done" on one download says
/// nothing about the four others still transferring, and the countdown has to be
/// able to say so before it pauses them.
/// </summary>
public sealed record CompletionPlan(bool RevealFolder, bool PlaySound, PowerAction Power, int StillRunning)
{
    public static CompletionPlan Nothing { get; } = new(false, false, PowerAction.None, 0);

    public bool HasPowerAction => Power != PowerAction.None;

    /// <summary>A download just completed.</summary>
    public static CompletionPlan ForDownload(DownloadItem completed, IEnumerable<DownloadItem> all)
    {
        if (completed.Status != DownloadStatus.Completed) return Nothing;

        return new CompletionPlan(
            completed.OnCompletion.HasFlag(CompletionActions.OpenFolder),
            completed.OnCompletion.HasFlag(CompletionActions.PlaySound),
            Defined(completed.PowerActionWhenDone),
            StillRunningExcept(completed.Id, all));
    }

    /// <summary>
    /// A queue's run ended. Only a run that <paramref name="drained"/> the queue
    /// counts as finishing it: a run the user stopped, or one its own stop time
    /// cut short, has files left, and shutting the machine down on top of that
    /// would be the opposite of what was asked.
    /// </summary>
    public static CompletionPlan ForQueue(DownloadQueue queue, bool drained, IEnumerable<DownloadItem> all)
    {
        if (!drained || Defined(queue.WhenFinished) == PowerAction.None) return Nothing;

        return new CompletionPlan(false, false, queue.WhenFinished, StillRunningExcept(null, all));
    }

    /// <summary>A value read from a hand-edited file that names no action is no action.</summary>
    public static PowerAction Defined(PowerAction action) =>
        Enum.IsDefined(action) ? action : PowerAction.None;

    private static int StillRunningExcept(Guid? id, IEnumerable<DownloadItem> all) =>
        all.Count(item => item.Id != id && DownloadActions.IsActive(item));
}
