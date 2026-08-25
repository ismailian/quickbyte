using QuickByte.Core.Models;

namespace QuickByte.Core.Events;

public enum QueueChangeType
{
    /// <summary>A queue was created.</summary>
    Added,

    /// <summary>A queue was deleted; its downloads are back to belonging to nothing.</summary>
    Removed,

    /// <summary>Name, concurrency, speed limit or schedule changed.</summary>
    Updated,

    /// <summary>Downloads joined, left or were reordered within the queue.</summary>
    MembershipChanged
}

/// <summary>
/// Raised by <see cref="Interfaces.IQueueManager"/> whenever the set of queues,
/// one queue's configuration, or its membership changes — the one signal the
/// sidebar, the queue window and the row context menu all rebuild from, for the
/// same reason every download window listens to one manager rather than to each
/// other.
/// </summary>
public sealed class QueuesChangedEventArgs : EventArgs
{
    public required QueueChangeType ChangeType { get; init; }

    /// <summary>The queue that changed; null only for <see cref="QueueChangeType.MembershipChanged"/> spanning several.</summary>
    public DownloadQueue? Queue { get; init; }
}
