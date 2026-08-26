using QuickByte.Core.Enums;

namespace QuickByte.Core.Models;

/// <summary>
/// A named, ordered collection of downloads with its own transfer settings — the
/// IDM idea of a queue. Persisted to queues.json by
/// <see cref="Interfaces.IQueueRepository"/> and driven by
/// <see cref="Services.QueueManager"/>.
///
/// Membership lives here, as an ordered list of download ids, rather than as a
/// <c>QueueId</c> on <see cref="DownloadItem"/>. Order is half of what a queue
/// <em>is</em>, and a field on the item could not express it; keeping both
/// halves in one place also means downloads.json and queues.json are never two
/// disagreeing answers to "what is in this queue".
/// </summary>
public sealed class DownloadQueue
{
    public const int MinConcurrentDownloads = 1;
    public const int MaxConcurrentDownloads = 20;
    public const int MaxNameLength = 60;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "New queue";

    /// <summary>
    /// How many of this queue's downloads run at once. Its own cap on top of
    /// <see cref="DownloadSettings.MaxConcurrentDownloads"/>, never instead of
    /// it: the application-wide gate still applies, so a queue asking for eight
    /// on an app configured for three gets three.
    /// </summary>
    public int ConcurrentDownloads { get; set; } = 1;

    /// <summary>
    /// Cap shared by this queue's running downloads, in bytes per second;
    /// <c>0</c> means unlimited. Applied as a third tier between a download's own
    /// limit and the global one — see
    /// <see cref="Interfaces.IDownloadManager.SetQueueSpeedLimit"/> — and lifted
    /// again the moment a download leaves the queue or the queue stops.
    /// </summary>
    public long SpeedLimitBytesPerSecond { get; set; }

    /// <remarks>
    /// The setter coalesces for the same reason <see cref="DownloadItem.Headers"/>
    /// does: an explicit <c>"Schedule": null</c> in a hand-edited or
    /// half-migrated file would otherwise beat the initializer, and every read
    /// below assumes a schedule object exists.
    /// </remarks>
    public QueueSchedule Schedule
    {
        get => _schedule;
        set => _schedule = value ?? new QueueSchedule();
    }

    private QueueSchedule _schedule = new();

    /// <summary>Download ids, in the order the queue will start them.</summary>
    public List<Guid> ItemIds
    {
        get => _itemIds;
        set => _itemIds = value ?? new List<Guid>();
    }

    private List<Guid> _itemIds = new();

    /// <summary>
    /// When this queue was last started by its schedule or by hand. It is what
    /// stops a run from being started twice inside one window — by the app's own
    /// scheduler, by the agent, and by a launch that arrives while the window is
    /// still open — so it is persisted rather than kept in memory.
    /// </summary>
    public DateTimeOffset? LastRunAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// Whether this queue is supposed to be starting at <paramref name="now"/>
    /// and has not already been started for that window. The single question
    /// both the in-app scheduler and the out-of-process agent ask, so that two
    /// watchers of one file cannot disagree about whether a run is owed.
    /// </summary>
    public bool IsDue(DateTime now) =>
        Schedule.WindowStart(now) is { } start &&
        (LastRunAt is null || LastRunAt.Value.LocalDateTime < start);

    /// <summary>
    /// When this queue will next start on its own: now (as a due window's start)
    /// if one is owed, otherwise the next scheduled occurrence. Null when the
    /// queue is not scheduled at all.
    /// </summary>
    public DateTime? NextRunAt(DateTime now) =>
        IsDue(now) ? Schedule.WindowStart(now) : Schedule.NextStart(now);

    public int ClampConcurrency() =>
        Math.Clamp(ConcurrentDownloads, MinConcurrentDownloads, MaxConcurrentDownloads);

    /// <summary>
    /// A detached copy for an editor to work on, so a half-finished edit is not
    /// already live on the queue the runner is reading.
    /// </summary>
    public DownloadQueue Clone() => new()
    {
        Id = Id,
        Name = Name,
        ConcurrentDownloads = ConcurrentDownloads,
        SpeedLimitBytesPerSecond = SpeedLimitBytesPerSecond,
        Schedule = Schedule.Clone(),
        ItemIds = new List<Guid>(ItemIds),
        LastRunAt = LastRunAt,
        CreatedAt = CreatedAt
    };
}
