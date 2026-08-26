using QuickByte.Core.Enums;
using QuickByte.Core.Events;
using QuickByte.Core.Models;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// Owns every <see cref="DownloadQueue"/>: creating and configuring them, what
/// is in them and in what order, running them, and starting them when their
/// schedule says so.
///
/// It sits <em>beside</em> <see cref="IDownloadManager"/> rather than inside it,
/// and drives downloads only through that interface. The download engine has no
/// idea queues exist — a queue is a policy about when to call Resume and how
/// fast to let the result go, which is exactly the amount of coupling the two
/// need.
///
/// Every event is raised on the UI thread through <see cref="IDispatcher"/>, as
/// the download manager's are: the schedule fires on a timer thread and a run
/// finishes on a thread-pool one.
/// </summary>
public interface IQueueManager : IDisposable
{
    IReadOnlyList<DownloadQueue> Queues { get; }

    event EventHandler<QueuesChangedEventArgs>? QueuesChanged;
    event EventHandler<QueueStateChangedEventArgs>? QueueStateChanged;

    /// <summary>
    /// Reads the persisted queues and starts the schedule timer. Call after
    /// <see cref="IDownloadManager.LoadPersistedDownloads"/> — a queue's
    /// membership is meaningless until the downloads it names exist.
    /// </summary>
    void Load();

    DownloadQueue Create(string name);

    /// <summary>
    /// Applies an edited copy of a queue: name, concurrency, speed limit and
    /// schedule. Membership is not read from it — that is
    /// <see cref="AddToQueue"/>'s and <see cref="Move"/>'s business, and an
    /// editor holding a stale copy must not be able to un-add a download that
    /// joined the queue while it was open.
    /// </summary>
    void Update(DownloadQueue edited);

    void Delete(Guid queueId);

    DownloadQueue? Find(Guid queueId);

    /// <summary>The queue a download belongs to, or null. A download is in at most one queue.</summary>
    DownloadQueue? QueueOf(Guid downloadId);

    /// <summary>
    /// Just the id of that queue. <see cref="QueueOf"/> hands back a detached
    /// copy of the whole queue, which is the wrong shape for the sidebar
    /// filters — they ask this question once per download on every refresh.
    /// </summary>
    Guid? QueueIdOf(Guid downloadId);

    /// <summary>
    /// Appends downloads to a queue, taking each one out of whatever queue it
    /// was in. Downloads already in this queue keep their position rather than
    /// being moved to the end.
    /// </summary>
    void AddToQueue(Guid queueId, IEnumerable<Guid> downloadIds);

    void RemoveFromQueues(IEnumerable<Guid> downloadIds);

    /// <summary>
    /// Moves a download <paramref name="offset"/> places within its queue
    /// (negative is earlier). Returns false if it did not move — already at
    /// the end it was heading for, or not in that queue.
    /// </summary>
    bool Move(Guid queueId, Guid downloadId, int offset);

    /// <summary>
    /// Starts the queue now: it walks its downloads in order, running up to
    /// <see cref="DownloadQueue.ConcurrentDownloads"/> at a time until nothing
    /// pending is left. Starting an already-running queue does nothing.
    /// </summary>
    void Start(Guid queueId);

    /// <summary>
    /// Starts a queue on behalf of the out-of-process scheduler agent, which has
    /// already decided the queue is due — see the <c>--run-queue</c> switch. The
    /// run counts as a scheduled one, so the queue's stop time applies to it.
    /// </summary>
    void StartFromScheduler(Guid queueId);

    /// <summary>
    /// Stops the run and pauses whatever it had in flight, leaving the chunk
    /// files in place so the next run continues rather than restarts.
    /// </summary>
    void Stop(Guid queueId);

    QueueState StateOf(Guid queueId);

    /// <summary>When the queue will next start on its own, or null if it is not scheduled.</summary>
    DateTime? NextRunAt(Guid queueId);

    /// <summary>
    /// Whether any queue has a schedule switched on — the one thing that decides
    /// whether the out-of-process scheduler agent needs to exist at all.
    /// </summary>
    bool HasScheduledQueues { get; }
}
