using QuickByte.Core.Enums;

namespace QuickByte.Core.Events;

/// <summary>
/// Raised when a queue starts or stops running, so the window that offers Start
/// and Stop can say which of the two is true without polling.
/// </summary>
public sealed class QueueStateChangedEventArgs : EventArgs
{
    public required Guid QueueId { get; init; }
    public required QueueState State { get; init; }

    /// <summary>
    /// True when the run ended because the queue ran out of downloads to start,
    /// rather than because it was stopped by the user or by its own stop time.
    /// </summary>
    public bool Drained { get; init; }
}
