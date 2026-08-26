using System.Threading;
using QuickByte.Core.Enums;
using QuickByte.Core.Events;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// Runs queues and keeps their schedules. See <see cref="IQueueManager"/> for
/// what it is for; this file is how.
///
/// <para><b>One runner per running queue.</b> A run is a loop rather than a
/// <c>Task.WhenAll</c> over a snapshot: it re-reads the queue on every pass, so
/// a download appended while the queue is running is picked up, a concurrency
/// change takes effect on the next free slot, and a stop time can end the run
/// between downloads instead of only between queues. Each download is started
/// at most once per run — the alternative is a queue that instantly restarts a
/// download the user just paused, and (when a start fails outright) a tight
/// loop.</para>
///
/// <para><b>Two schedulers, one file.</b> The timer here starts due queues while
/// the app is open. When it is not, <c>QuickByte.Agent</c> — a separate process
/// that outlives it — reads the same queues.json, reaches the same verdict
/// through <see cref="DownloadQueue.IsDue"/>, and launches QuickByte with
/// <c>--run-queue</c>. <see cref="DownloadQueue.LastRunAt"/> is persisted the
/// moment a run starts, which is what stops the two from starting the same
/// window twice.</para>
/// </summary>
public sealed class QueueManager : IQueueManager
{
    /// <summary>
    /// How often due queues are looked for. Schedules have minute resolution, so
    /// this only has to be comfortably below a minute; it costs one pass over a
    /// handful of objects.
    /// </summary>
    private static readonly TimeSpan ScheduleTickInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Longest a runner waits before re-reading its queue while it has downloads
    /// in flight. Short enough that a stop time, a concurrency change or a newly
    /// appended download is acted on promptly.
    /// </summary>
    private const int RunnerPollMilliseconds = 1000;

    private readonly IQueueRepository _repository;
    private readonly IDownloadManager _downloads;
    private readonly IDispatcher _dispatcher;

    private readonly object _sync = new();
    private readonly List<DownloadQueue> _queues = new();

    /// <summary>Download id -> queue id. Rebuilt on every membership change; a download is in at most one queue.</summary>
    private readonly Dictionary<Guid, Guid> _membership = new();

    private readonly Dictionary<Guid, QueueRun> _runs = new();

    private Timer? _scheduleTimer;

    /// <summary>Guards against a slow tick overlapping the next one.</summary>
    private int _tickInProgress;

    private bool _disposed;

    public event EventHandler<QueuesChangedEventArgs>? QueuesChanged;
    public event EventHandler<QueueStateChangedEventArgs>? QueueStateChanged;

    public QueueManager(IQueueRepository repository, IDownloadManager downloads, IDispatcher dispatcher)
    {
        _repository = repository;
        _downloads = downloads;
        _dispatcher = dispatcher;

        // A download that is deleted has to leave its queue with it, or the queue
        // keeps a slot for a file that no longer exists and the runner walks past
        // an id it can never start.
        _downloads.DownloadListChanged += OnDownloadListChanged;
    }

    public IReadOnlyList<DownloadQueue> Queues
    {
        get { lock (_sync) return _queues.ToList(); }
    }

    public bool HasScheduledQueues
    {
        get { lock (_sync) return _queues.Any(queue => queue.Schedule.Enabled); }
    }

    public void Load()
    {
        lock (_sync)
        {
            _queues.Clear();
            _queues.AddRange(_repository.LoadAll());
            ReindexMembership();
        }

        _scheduleTimer = new Timer(_ => OnScheduleTick(), null, ScheduleTickInterval, ScheduleTickInterval);
    }

    // ------------------------------------------------------------- CRUD --

    public DownloadQueue Create(string name)
    {
        var queue = new DownloadQueue { Name = SanitizeName(name) };

        lock (_sync)
        {
            _queues.Add(queue);
            Persist();
        }

        RaiseQueuesChanged(QueueChangeType.Added, queue);
        return queue;
    }

    public void Update(DownloadQueue edited)
    {
        DownloadQueue? queue;
        long speedLimit;

        lock (_sync)
        {
            queue = FindLocked(edited.Id);
            if (queue is null) return;

            queue.Name = SanitizeName(edited.Name);
            queue.ConcurrentDownloads = Math.Clamp(
                edited.ConcurrentDownloads, DownloadQueue.MinConcurrentDownloads, DownloadQueue.MaxConcurrentDownloads);
            queue.SpeedLimitBytesPerSecond = Math.Max(0, edited.SpeedLimitBytesPerSecond);
            queue.Schedule = edited.Schedule.Clone();
            speedLimit = queue.SpeedLimitBytesPerSecond;

            Persist();
        }

        // A speed limit changed while the queue is running applies to the
        // transfers already in flight, for the same reason the global one does:
        // a limit you have to restart a download to apply is not a limit. Only
        // the ones actually transferring are touched — the rest are given the
        // new limit as the queue reaches them.
        if (IsRunning(edited.Id))
        {
            foreach (var id in ItemsOf(edited.Id))
            {
                var item = FindItem(id);
                if (item is not null && DownloadActions.CanPause(item))
                    _downloads.SetQueueSpeedLimit(id, speedLimit);
            }
        }

        RaiseQueuesChanged(QueueChangeType.Updated, queue);
    }

    public void Delete(Guid queueId)
    {
        Stop(queueId);

        DownloadQueue? removed;
        lock (_sync)
        {
            removed = FindLocked(queueId);
            if (removed is null) return;

            _queues.Remove(removed);
            ReindexMembership();
            Persist();
        }

        // The queue's cap dies with it — its downloads are ordinary downloads now.
        foreach (var id in removed.ItemIds)
            _downloads.SetQueueSpeedLimit(id, 0);

        RaiseQueuesChanged(QueueChangeType.Removed, removed);
    }

    public DownloadQueue? Find(Guid queueId)
    {
        lock (_sync) return FindLocked(queueId)?.Clone();
    }

    public DownloadQueue? QueueOf(Guid downloadId)
    {
        lock (_sync)
        {
            return _membership.TryGetValue(downloadId, out var queueId)
                ? FindLocked(queueId)?.Clone()
                : null;
        }
    }

    public Guid? QueueIdOf(Guid downloadId)
    {
        lock (_sync) return _membership.TryGetValue(downloadId, out var queueId) ? queueId : null;
    }

    // ------------------------------------------------------- Membership --

    public void AddToQueue(Guid queueId, IEnumerable<Guid> downloadIds)
    {
        var ids = downloadIds.Distinct().ToList();
        if (ids.Count == 0) return;

        DownloadQueue? queue;
        lock (_sync)
        {
            queue = FindLocked(queueId);
            if (queue is null) return;

            foreach (var id in ids)
            {
                if (_membership.TryGetValue(id, out var currentQueueId))
                {
                    if (currentQueueId == queueId) continue; // already here, keep its place
                    FindLocked(currentQueueId)?.ItemIds.Remove(id);
                }
                queue.ItemIds.Add(id);
            }

            ReindexMembership();
            Persist();
        }

        RaiseQueuesChanged(QueueChangeType.MembershipChanged, queue);
    }

    public void RemoveFromQueues(IEnumerable<Guid> downloadIds)
    {
        var ids = downloadIds.Distinct().ToList();
        if (ids.Count == 0) return;

        bool changed = false;
        lock (_sync)
        {
            foreach (var id in ids)
            {
                if (!_membership.TryGetValue(id, out var queueId)) continue;
                if (FindLocked(queueId)?.ItemIds.Remove(id) == true) changed = true;
            }

            if (!changed) return;
            ReindexMembership();
            Persist();
        }

        foreach (var id in ids)
            _downloads.SetQueueSpeedLimit(id, 0);

        RaiseQueuesChanged(QueueChangeType.MembershipChanged, null);
    }

    public bool Move(Guid queueId, Guid downloadId, int offset)
    {
        if (offset == 0) return false;

        DownloadQueue? queue;
        lock (_sync)
        {
            queue = FindLocked(queueId);
            if (queue is null) return false;

            int from = queue.ItemIds.IndexOf(downloadId);
            if (from < 0) return false;

            int to = Math.Clamp(from + offset, 0, queue.ItemIds.Count - 1);
            if (to == from) return false;

            queue.ItemIds.RemoveAt(from);
            queue.ItemIds.Insert(to, downloadId);
            Persist();
        }

        RaiseQueuesChanged(QueueChangeType.MembershipChanged, queue);
        return true;
    }

    private void OnDownloadListChanged(object? sender, DownloadListChangedEventArgs e)
    {
        if (e.ChangeType != DownloadListChangeType.Removed) return;
        RemoveFromQueues(new[] { e.Item.Id });
    }

    // ---------------------------------------------------------- Running --

    public void Start(Guid queueId) => StartInternal(queueId, scheduled: false);

    private void StartInternal(Guid queueId, bool scheduled)
    {
        DownloadQueue? queue;

        lock (_sync)
        {
            queue = FindLocked(queueId);
            if (queue is null || _runs.ContainsKey(queueId)) return;

            var run = new QueueRun(scheduled);
            _runs[queueId] = run;

            // Written before the first download starts, and persisted: it is what
            // tells this app's next tick — and the agent reading the same file —
            // that this window's run is already under way.
            queue.LastRunAt = DateTimeOffset.Now;
            Persist();

            _ = Task.Run(() => RunAsync(queueId, run.Cancellation.Token));
        }

        RaiseStateChanged(queueId, QueueState.Running, drained: false);
    }

    public void Stop(Guid queueId)
    {
        QueueRun? run;
        lock (_sync)
        {
            if (!_runs.TryGetValue(queueId, out run)) return;
        }

        // The runner disposes its own token source when it unwinds, and it may
        // have unwound between the lookup above and this line.
        try { run.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }

        // Pause rather than stop: the run is over, the downloads are not. Their
        // chunk files stay on disk so the next run continues them.
        foreach (var id in ItemsOf(queueId))
        {
            PauseIfRunning(id);
            _downloads.SetQueueSpeedLimit(id, 0);
        }
    }

    private void PauseIfRunning(Guid downloadId)
    {
        var item = FindItem(downloadId);
        if (item is not null && DownloadActions.CanPause(item)) _downloads.Pause(downloadId);
    }

    public QueueState StateOf(Guid queueId)
    {
        lock (_sync) return _runs.ContainsKey(queueId) ? QueueState.Running : QueueState.Idle;
    }

    public DateTime? NextRunAt(Guid queueId)
    {
        lock (_sync) return FindLocked(queueId)?.NextRunAt(DateTime.Now);
    }

    private bool IsRunning(Guid queueId)
    {
        lock (_sync) return _runs.ContainsKey(queueId);
    }

    /// <summary>
    /// Walks one queue until it runs out of downloads to start. Only ever
    /// touches the engine through <see cref="IDownloadManager"/>, so the
    /// application-wide concurrency gate and the global speed limit still apply
    /// on top of the queue's own.
    /// </summary>
    private async Task RunAsync(Guid queueId, CancellationToken token)
    {
        // Started once each, whatever they do next: a download that fails, or
        // that the user pauses by hand, is not started again by this run.
        var attempted = new HashSet<Guid>();
        var active = new Dictionary<Guid, Task>();
        bool drained = false;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var queue = Find(queueId);
                if (queue is null) break;

                // Only a run its own schedule started answers to that schedule's
                // stop time; one the user started by hand runs until it is done.
                if (IsRunScheduled(queueId) && queue.Schedule.StopAtEnabled &&
                    queue.Schedule.WindowStart(DateTime.Now) is null)
                    break;

                Guid? next = active.Count < queue.ClampConcurrency()
                    ? NextPending(queue, attempted)
                    : null;

                if (next is Guid id)
                {
                    attempted.Add(id);
                    _downloads.SetQueueSpeedLimit(id, queue.SpeedLimitBytesPerSecond);
                    active[id] = StartOneAsync(id);
                    continue;
                }

                if (active.Count == 0)
                {
                    drained = true;
                    break;
                }

                var waits = new List<Task>(active.Values) { Task.Delay(RunnerPollMilliseconds, token) };
                await Task.WhenAny(waits).ConfigureAwait(false);

                foreach (var finished in active.Where(pair => pair.Value.IsCompleted).Select(pair => pair.Key).ToList())
                {
                    active.Remove(finished);
                    _downloads.SetQueueSpeedLimit(finished, 0);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stop() — the downloads have already been paused by the caller.
        }
        finally
        {
            // Left the loop with downloads still in flight, and not because the
            // run was cancelled: the stop time arrived. Nothing else will pause
            // them — the scheduler tick only stops a run that is still
            // registered, and this one is about to deregister itself — so a
            // queue that promised to stop at 06:00 has to do it here.
            bool pauseActive = !drained && !token.IsCancellationRequested;

            foreach (var id in active.Keys)
            {
                if (pauseActive) PauseIfRunning(id);
                _downloads.SetQueueSpeedLimit(id, 0);
            }

            lock (_sync)
            {
                if (_runs.TryGetValue(queueId, out var run))
                {
                    _runs.Remove(queueId);
                    run.Cancellation.Dispose();
                }
            }
            RaiseStateChanged(queueId, QueueState.Idle, drained);
        }
    }

    /// <summary>
    /// Runs one download to completion, absorbing whatever it ends as. A queue
    /// is a sequence, not a transaction: a link that 404s must not take the rest
    /// of the queue down with it, and the download's own status already records
    /// what happened.
    /// </summary>
    private async Task StartOneAsync(Guid downloadId)
    {
        try
        {
            await _downloads.ResumeAsync(downloadId).ConfigureAwait(false);
        }
        catch
        {
            // Recorded on the item as Failed by the service itself.
        }
    }

    /// <summary>
    /// The first download in queue order that is waiting to run and has not been
    /// started by this run yet. Completed, failed and cancelled downloads are
    /// skipped: a queue is a to-do list, not a retry loop.
    /// </summary>
    private Guid? NextPending(DownloadQueue queue, HashSet<Guid> attempted)
    {
        var items = _downloads.Downloads.ToDictionary(item => item.Id);

        foreach (var id in queue.ItemIds)
        {
            if (attempted.Contains(id)) continue;
            if (!items.TryGetValue(id, out var item)) continue;
            if (item.Status is DownloadStatus.Queued or DownloadStatus.Paused) return id;
        }
        return null;
    }

    private bool IsRunScheduled(Guid queueId)
    {
        lock (_sync) return _runs.TryGetValue(queueId, out var run) && run.Scheduled;
    }

    // -------------------------------------------------------- Scheduling --

    private void OnScheduleTick()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _tickInProgress, 1) == 1) return;

        try
        {
            var now = DateTime.Now;
            foreach (var queue in Queues)
            {
                if (StateOf(queue.Id) == QueueState.Running)
                {
                    // A scheduled run that has reached its stop time. The runner
                    // notices too, but only between downloads; this is what ends
                    // a run whose last download would otherwise carry it past the
                    // window on its own.
                    if (IsRunScheduled(queue.Id) && queue.Schedule.StopAtEnabled &&
                        queue.Schedule.WindowStart(now) is null)
                        Stop(queue.Id);
                    continue;
                }

                if (queue.IsDue(now)) StartInternal(queue.Id, scheduled: true);
            }
        }
        catch
        {
            // A tick that throws must not kill the timer — the next schedule
            // still deserves to fire.
        }
        finally
        {
            Interlocked.Exchange(ref _tickInProgress, 0);
        }
    }

    /// <summary>
    /// Starts a queue because something outside the app said its time had come —
    /// the scheduler agent, via <c>--run-queue</c> on the command line. Treated
    /// as a scheduled run (so its stop time applies) but started even if the
    /// window has since closed: the agent already decided, and the launch it
    /// asked for took time to arrive.
    /// </summary>
    public void StartFromScheduler(Guid queueId) => StartInternal(queueId, scheduled: true);

    // ------------------------------------------------------------- Bits --

    private DownloadQueue? FindLocked(Guid queueId) => _queues.FirstOrDefault(queue => queue.Id == queueId);

    private List<Guid> ItemsOf(Guid queueId)
    {
        lock (_sync) return FindLocked(queueId)?.ItemIds.ToList() ?? new List<Guid>();
    }

    private DownloadItem? FindItem(Guid downloadId) =>
        _downloads.Downloads.FirstOrDefault(item => item.Id == downloadId);

    private void ReindexMembership()
    {
        _membership.Clear();
        foreach (var queue in _queues)
        {
            foreach (var id in queue.ItemIds)
                _membership[id] = queue.Id;
        }
    }

    private void Persist() => _repository.SaveAll(_queues);

    private static string SanitizeName(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length == 0) return "New queue";
        return trimmed.Length > DownloadQueue.MaxNameLength
            ? trimmed[..DownloadQueue.MaxNameLength]
            : trimmed;
    }

    private void RaiseQueuesChanged(QueueChangeType changeType, DownloadQueue? queue)
    {
        // Cloned under the lock. Every caller reaches here holding a reference to
        // the *live* queue and having already left the lock, so copying its
        // ItemIds list races anything editing membership on another thread —
        // List<T>'s copy constructor reads Count and then CopyTo.
        DownloadQueue? snapshot;
        lock (_sync) snapshot = queue?.Clone();
        _dispatcher.Post(() => QueuesChanged?.Invoke(this,
            new QueuesChangedEventArgs { ChangeType = changeType, Queue = snapshot }));
    }

    private void RaiseStateChanged(Guid queueId, QueueState state, bool drained)
    {
        _dispatcher.Post(() => QueueStateChanged?.Invoke(this,
            new QueueStateChangedEventArgs { QueueId = queueId, State = state, Drained = drained }));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _downloads.DownloadListChanged -= OnDownloadListChanged;
        _scheduleTimer?.Dispose();

        List<QueueRun> runs;
        lock (_sync)
        {
            runs = _runs.Values.ToList();
            _runs.Clear();
        }

        // Cancelled, not stopped: shutdown pauses every download through
        // IDownloadManager.PauseAll() anyway, and doing it twice would race with
        // the close. The token sources are deliberately not disposed here — the
        // runners still hold their tokens, and this is the last thing that
        // happens to them before the process ends.
        foreach (var run in runs)
            run.Cancellation.Cancel();
    }

    /// <summary>One queue's in-flight run.</summary>
    private sealed class QueueRun
    {
        public QueueRun(bool scheduled) => Scheduled = scheduled;

        /// <summary>True when the schedule started this run, not the user — only those obey a stop time.</summary>
        public bool Scheduled { get; }

        public CancellationTokenSource Cancellation { get; } = new();
    }
}
