namespace QuickByte.Core.Enums;

/// <summary>
/// What a queue is doing right now. Unlike <see cref="DownloadStatus"/> this is
/// never persisted: a queue that was running when the app closed is Idle when it
/// comes back, and its schedule — or the user — starts it again.
/// </summary>
public enum QueueState
{
    /// <summary>Not running. Either drained, never started, or waiting for its schedule.</summary>
    Idle,

    /// <summary>A runner is walking the queue, starting downloads as slots free up.</summary>
    Running
}
