using QuickByte.Core.Enums;
using QuickByte.Core.Events;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.Core.Services;

namespace QuickByte.Core.Tests.Services;

/// <summary>
/// Queue membership, ordering and running, against a fake download manager.
///
/// The runner is a loop rather than a WhenAll over a snapshot, so the tests that
/// exercise it wait on an outcome rather than on a clock — a queue drains when
/// it runs out of downloads to start, which is an event, not a duration.
/// </summary>
public sealed class QueueManagerTests
{
    // ------------------------------------------------------------- CRUD --

    [Fact]
    public void A_new_queue_is_persisted_and_announced()
    {
        using var fixture = new Fixture();
        var changes = new List<QueueChangeType>();
        fixture.Manager.QueuesChanged += (_, e) => changes.Add(e.ChangeType);

        var queue = fixture.Manager.Create("Nightly");

        Assert.Equal("Nightly", queue.Name);
        Assert.Equal(QueueChangeType.Added, Assert.Single(changes));
        Assert.Single(fixture.Repository.Saved);
    }

    [Theory]
    [InlineData("", "New queue")]
    [InlineData("   ", "New queue")]
    [InlineData("  Nightly  ", "Nightly")]
    public void A_queue_name_is_tidied_up(string given, string expected)
    {
        using var fixture = new Fixture();

        Assert.Equal(expected, fixture.Manager.Create(given).Name);
    }

    [Fact]
    public void A_very_long_queue_name_is_cut_to_length()
    {
        using var fixture = new Fixture();

        var queue = fixture.Manager.Create(new string('x', 200));

        Assert.Equal(DownloadQueue.MaxNameLength, queue.Name.Length);
    }

    [Fact]
    public void Update_clamps_what_it_is_given()
    {
        using var fixture = new Fixture();
        var queue = fixture.Manager.Create("Nightly");

        var edited = queue.Clone();
        edited.ConcurrentDownloads = 999;
        edited.SpeedLimitBytesPerSecond = -5;
        fixture.Manager.Update(edited);

        var stored = fixture.Manager.Find(queue.Id)!;
        Assert.Equal(DownloadQueue.MaxConcurrentDownloads, stored.ConcurrentDownloads);
        Assert.Equal(0, stored.SpeedLimitBytesPerSecond);
    }

    [Fact]
    public void Update_of_a_queue_that_is_gone_is_harmless()
    {
        using var fixture = new Fixture();

        fixture.Manager.Update(new DownloadQueue { Name = "Ghost" });

        Assert.Empty(fixture.Manager.Queues);
    }

    [Fact]
    public void Find_hands_back_a_copy_rather_than_the_live_queue()
    {
        // So a half-finished edit is not already live on the queue the runner is
        // reading.
        using var fixture = new Fixture();
        var queue = fixture.Manager.Create("Nightly");

        var found = fixture.Manager.Find(queue.Id)!;
        found.Name = "Edited";

        Assert.Equal("Nightly", fixture.Manager.Find(queue.Id)!.Name);
    }

    [Fact]
    public void Deleting_a_queue_lifts_its_speed_limit_from_its_downloads()
    {
        // The queue's cap dies with it — its downloads are ordinary downloads now.
        using var fixture = new Fixture();
        var item = fixture.Downloads.Add(DownloadStatus.Queued);
        var queue = fixture.Manager.Create("Nightly");
        fixture.Manager.AddToQueue(queue.Id, new[] { item.Id });

        fixture.Manager.Delete(queue.Id);

        Assert.Empty(fixture.Manager.Queues);
        Assert.Contains((item.Id, 0L), fixture.Downloads.QueueSpeedLimits);
    }

    // ------------------------------------------------------- membership --

    [Fact]
    public void A_download_belongs_to_at_most_one_queue()
    {
        using var fixture = new Fixture();
        var item = fixture.Downloads.Add(DownloadStatus.Queued);
        var first = fixture.Manager.Create("First");
        var second = fixture.Manager.Create("Second");

        fixture.Manager.AddToQueue(first.Id, new[] { item.Id });
        fixture.Manager.AddToQueue(second.Id, new[] { item.Id });

        Assert.Empty(fixture.Manager.Find(first.Id)!.ItemIds);
        Assert.Single(fixture.Manager.Find(second.Id)!.ItemIds);
        Assert.Equal(second.Id, fixture.Manager.QueueIdOf(item.Id));
    }

    [Fact]
    public void Re_adding_a_download_to_the_queue_it_is_in_keeps_its_place()
    {
        using var fixture = new Fixture();
        var a = fixture.Downloads.Add(DownloadStatus.Queued);
        var b = fixture.Downloads.Add(DownloadStatus.Queued);
        var queue = fixture.Manager.Create("Nightly");
        fixture.Manager.AddToQueue(queue.Id, new[] { a.Id, b.Id });

        fixture.Manager.AddToQueue(queue.Id, new[] { a.Id });

        Assert.Equal(new[] { a.Id, b.Id }, fixture.Manager.Find(queue.Id)!.ItemIds);
    }

    [Fact]
    public void Adding_the_same_download_twice_in_one_call_adds_it_once()
    {
        using var fixture = new Fixture();
        var item = fixture.Downloads.Add(DownloadStatus.Queued);
        var queue = fixture.Manager.Create("Nightly");

        fixture.Manager.AddToQueue(queue.Id, new[] { item.Id, item.Id });

        Assert.Single(fixture.Manager.Find(queue.Id)!.ItemIds);
    }

    [Fact]
    public void A_deleted_download_leaves_its_queue_with_it()
    {
        // Or the queue keeps a slot for a file that no longer exists and the
        // runner walks past an id it can never start.
        using var fixture = new Fixture();
        var item = fixture.Downloads.Add(DownloadStatus.Queued);
        var queue = fixture.Manager.Create("Nightly");
        fixture.Manager.AddToQueue(queue.Id, new[] { item.Id });

        fixture.Downloads.RaiseRemoved(item);

        Assert.Empty(fixture.Manager.Find(queue.Id)!.ItemIds);
        Assert.Null(fixture.Manager.QueueIdOf(item.Id));
    }

    [Fact]
    public void Move_reorders_within_the_queue()
    {
        using var fixture = new Fixture();
        var ids = Enumerable.Range(0, 4).Select(_ => fixture.Downloads.Add(DownloadStatus.Queued).Id).ToArray();
        var queue = fixture.Manager.Create("Nightly");
        fixture.Manager.AddToQueue(queue.Id, ids);

        Assert.True(fixture.Manager.Move(queue.Id, ids[2], -1));

        Assert.Equal(new[] { ids[0], ids[2], ids[1], ids[3] }, fixture.Manager.Find(queue.Id)!.ItemIds);
    }

    [Fact]
    public void Move_clamps_at_the_ends_and_reports_when_nothing_happened()
    {
        using var fixture = new Fixture();
        var ids = Enumerable.Range(0, 3).Select(_ => fixture.Downloads.Add(DownloadStatus.Queued).Id).ToArray();
        var queue = fixture.Manager.Create("Nightly");
        fixture.Manager.AddToQueue(queue.Id, ids);

        Assert.False(fixture.Manager.Move(queue.Id, ids[0], -1));
        Assert.False(fixture.Manager.Move(queue.Id, ids[2], 5));
        Assert.False(fixture.Manager.Move(queue.Id, ids[0], 0));
        Assert.False(fixture.Manager.Move(queue.Id, Guid.NewGuid(), 1));
        Assert.True(fixture.Manager.Move(queue.Id, ids[0], 5));

        Assert.Equal(new[] { ids[1], ids[2], ids[0] }, fixture.Manager.Find(queue.Id)!.ItemIds);
    }

    // ---------------------------------------------------------- running --

    [Fact]
    public async Task A_run_starts_the_queue_s_downloads_in_order()
    {
        using var fixture = new Fixture();
        var ids = Enumerable.Range(0, 3).Select(_ => fixture.Downloads.Add(DownloadStatus.Queued).Id).ToArray();
        var queue = fixture.Manager.Create("Nightly");
        fixture.Manager.AddToQueue(queue.Id, ids);

        fixture.Manager.Start(queue.Id);
        await fixture.WaitForIdle(queue.Id);

        Assert.Equal(ids, fixture.Downloads.Resumed);
    }

    [Fact]
    public async Task A_drained_queue_says_so()
    {
        using var fixture = new Fixture();
        var queue = fixture.Manager.Create("Nightly");
        fixture.Manager.AddToQueue(queue.Id, new[] { fixture.Downloads.Add(DownloadStatus.Queued).Id });

        fixture.Manager.Start(queue.Id);
        await fixture.WaitForIdle(queue.Id);

        Assert.True(fixture.StateChanges.Last().Drained);
        Assert.Equal(QueueState.Idle, fixture.Manager.StateOf(queue.Id));
    }

    [Fact]
    public async Task A_run_skips_downloads_that_are_already_finished()
    {
        // A queue is a to-do list, not a retry loop.
        using var fixture = new Fixture();
        var completed = fixture.Downloads.Add(DownloadStatus.Completed);
        var failed = fixture.Downloads.Add(DownloadStatus.Failed);
        var pending = fixture.Downloads.Add(DownloadStatus.Paused);
        var queue = fixture.Manager.Create("Nightly");
        fixture.Manager.AddToQueue(queue.Id, new[] { completed.Id, failed.Id, pending.Id });

        fixture.Manager.Start(queue.Id);
        await fixture.WaitForIdle(queue.Id);

        Assert.Equal(new[] { pending.Id }, fixture.Downloads.Resumed);
    }

    [Fact]
    public async Task A_download_is_started_at_most_once_per_run()
    {
        // Otherwise the queue instantly restarts a download the user just paused,
        // and a start that fails outright becomes a tight loop.
        using var fixture = new Fixture();
        var item = fixture.Downloads.Add(DownloadStatus.Queued);
        fixture.Downloads.LeaveQueuedAfterResume = true;
        var queue = fixture.Manager.Create("Nightly");
        fixture.Manager.AddToQueue(queue.Id, new[] { item.Id });

        fixture.Manager.Start(queue.Id);
        await fixture.WaitForIdle(queue.Id);

        Assert.Single(fixture.Downloads.Resumed);
    }

    [Fact]
    public async Task Starting_a_queue_that_is_already_running_does_nothing()
    {
        using var fixture = new Fixture();
        var item = fixture.Downloads.Add(DownloadStatus.Queued);
        fixture.Downloads.BlockResume = true;
        var queue = fixture.Manager.Create("Nightly");
        fixture.Manager.AddToQueue(queue.Id, new[] { item.Id });

        fixture.Manager.Start(queue.Id);
        await fixture.WaitFor(() => fixture.Downloads.Resumed.Count == 1);
        fixture.Manager.Start(queue.Id);

        Assert.Single(fixture.Downloads.Resumed);

        fixture.Downloads.ReleaseResume();
        await fixture.WaitForIdle(queue.Id);
    }

    [Fact]
    public async Task A_run_honours_the_queue_s_own_concurrency()
    {
        using var fixture = new Fixture();
        var ids = Enumerable.Range(0, 5).Select(_ => fixture.Downloads.Add(DownloadStatus.Queued).Id).ToArray();
        fixture.Downloads.BlockResume = true;
        var queue = fixture.Manager.Create("Nightly");
        fixture.Manager.AddToQueue(queue.Id, ids);

        var edited = fixture.Manager.Find(queue.Id)!;
        edited.ConcurrentDownloads = 2;
        fixture.Manager.Update(edited);

        fixture.Manager.Start(queue.Id);
        await fixture.WaitFor(() => fixture.Downloads.Resumed.Count == 2);

        // Two slots, five downloads: the third waits for one to finish.
        await Task.Delay(50);
        Assert.Equal(2, fixture.Downloads.Resumed.Count);

        fixture.Downloads.ReleaseResume();
        await fixture.WaitForIdle(queue.Id);
        Assert.Equal(5, fixture.Downloads.Resumed.Count);
    }

    [Fact]
    public async Task A_run_applies_the_queue_s_speed_limit_and_lifts_it_afterwards()
    {
        using var fixture = new Fixture();
        var item = fixture.Downloads.Add(DownloadStatus.Queued);
        var queue = fixture.Manager.Create("Nightly");
        fixture.Manager.AddToQueue(queue.Id, new[] { item.Id });

        var edited = fixture.Manager.Find(queue.Id)!;
        edited.SpeedLimitBytesPerSecond = 100_000;
        fixture.Manager.Update(edited);

        fixture.Manager.Start(queue.Id);
        await fixture.WaitForIdle(queue.Id);

        Assert.Contains((item.Id, 100_000L), fixture.Downloads.QueueSpeedLimits);
        Assert.Equal((item.Id, 0L), fixture.Downloads.QueueSpeedLimits.Last());
    }

    [Fact]
    public async Task Stopping_a_queue_pauses_its_downloads_rather_than_discarding_them()
    {
        // The run is over, the downloads are not. Their chunk files stay on disk
        // so the next run continues them.
        using var fixture = new Fixture();
        var item = fixture.Downloads.Add(DownloadStatus.Queued);
        fixture.Downloads.BlockResume = true;
        var queue = fixture.Manager.Create("Nightly");
        fixture.Manager.AddToQueue(queue.Id, new[] { item.Id });

        fixture.Manager.Start(queue.Id);
        await fixture.WaitFor(() => fixture.Downloads.Resumed.Count == 1);
        item.Status = DownloadStatus.Downloading;

        fixture.Manager.Stop(queue.Id);

        Assert.Contains(item.Id, fixture.Downloads.Paused);
        Assert.Empty(fixture.Downloads.Stopped);

        fixture.Downloads.ReleaseResume();
        await fixture.WaitForIdle(queue.Id);
    }

    [Fact]
    public void Stopping_a_queue_that_is_not_running_is_harmless()
    {
        using var fixture = new Fixture();
        var queue = fixture.Manager.Create("Nightly");

        fixture.Manager.Stop(queue.Id);

        Assert.Equal(QueueState.Idle, fixture.Manager.StateOf(queue.Id));
    }

    [Fact]
    public void Starting_a_run_stamps_the_window_so_nothing_starts_it_twice()
    {
        // Persisted the moment a run starts: it is what tells this app's next tick
        // — and the agent reading the same file — that this window is under way.
        using var fixture = new Fixture();
        var queue = fixture.Manager.Create("Nightly");

        fixture.Manager.Start(queue.Id);

        Assert.NotNull(fixture.Manager.Find(queue.Id)!.LastRunAt);
        Assert.NotNull(fixture.Repository.Saved.Last().Single().LastRunAt);
    }

    [Fact]
    public void HasScheduledQueues_reports_whether_the_agent_is_worth_running()
    {
        using var fixture = new Fixture();
        var queue = fixture.Manager.Create("Nightly");
        Assert.False(fixture.Manager.HasScheduledQueues);

        var edited = fixture.Manager.Find(queue.Id)!;
        edited.Schedule.Enabled = true;
        fixture.Manager.Update(edited);

        Assert.True(fixture.Manager.HasScheduledQueues);
    }

    [Fact]
    public void NextRunAt_comes_from_the_queue_s_own_schedule()
    {
        using var fixture = new Fixture();
        var queue = fixture.Manager.Create("Nightly");
        var edited = fixture.Manager.Find(queue.Id)!;
        edited.Schedule.Enabled = true;
        edited.Schedule.Days = ScheduleDays.EveryDay;
        edited.Schedule.StartTime = TimeSpan.FromHours(2);
        fixture.Manager.Update(edited);

        Assert.NotNull(fixture.Manager.NextRunAt(queue.Id));
        Assert.Null(fixture.Manager.NextRunAt(Guid.NewGuid()));
    }

    [Fact]
    public void Load_reads_the_queues_off_disk()
    {
        using var fixture = new Fixture();
        var stored = new DownloadQueue { Name = "Nightly" };
        stored.ItemIds.Add(Guid.NewGuid());
        fixture.Repository.Stored.Add(stored);

        fixture.Manager.Load();

        Assert.Equal("Nightly", fixture.Manager.Queues.Single().Name);
        Assert.Equal(stored.Id, fixture.Manager.QueueIdOf(stored.ItemIds[0]));
    }

    // ------------------------------------------------------------- plumbing --

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Repository = new FakeQueueRepository();
            Downloads = new FakeDownloadManager();
            Manager = new QueueManager(Repository, Downloads, new InlineDispatcher());
            Manager.QueueStateChanged += (_, e) => StateChanges.Add(e);
        }

        public FakeQueueRepository Repository { get; }
        public FakeDownloadManager Downloads { get; }
        public QueueManager Manager { get; }
        public List<QueueStateChangedEventArgs> StateChanges { get; } = new();

        public Task WaitForIdle(Guid queueId) => WaitFor(() => Manager.StateOf(queueId) == QueueState.Idle);

        public async Task WaitFor(Func<bool> condition)
        {
            for (int i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
            Assert.True(condition(), "the queue did not reach the expected state in time");
        }

        public void Dispose() => Manager.Dispose();
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class FakeQueueRepository : IQueueRepository
    {
        public List<DownloadQueue> Stored { get; } = new();
        public List<List<DownloadQueue>> Saved { get; } = new();

        public List<DownloadQueue> LoadAll() => Stored.ToList();

        public bool TryLoadAll(out List<DownloadQueue> queues)
        {
            queues = Stored.ToList();
            return true;
        }

        public void SaveAll(IEnumerable<DownloadQueue> queues) =>
            Saved.Add(queues.Select(q => q.Clone()).ToList());
    }

    private sealed class FakeDownloadManager : IDownloadManager
    {
        private readonly List<DownloadItem> _items = new();
        private readonly TaskCompletionSource _release = new();

        public List<Guid> Resumed { get; } = new();
        public List<Guid> Paused { get; } = new();
        public List<Guid> Stopped { get; } = new();
        public List<(Guid Id, long Limit)> QueueSpeedLimits { get; } = new();

        /// <summary>Holds ResumeAsync open so concurrency and Stop can be observed.</summary>
        public bool BlockResume { get; set; }

        /// <summary>Leaves the download pending after a resume, as a failed start would.</summary>
        public bool LeaveQueuedAfterResume { get; set; }

        public DownloadItem Add(DownloadStatus status)
        {
            var item = new DownloadItem { Status = status };
            _items.Add(item);
            return item;
        }

        /// <summary>Lets every held resume through, and every later one straight past.</summary>
        public void ReleaseResume()
        {
            BlockResume = false;
            _release.TrySetResult();
        }

        public void RaiseRemoved(DownloadItem item)
        {
            _items.Remove(item);
            DownloadListChanged?.Invoke(this, new DownloadListChangedEventArgs
            {
                ChangeType = DownloadListChangeType.Removed,
                Item = item
            });
        }

        public IReadOnlyList<DownloadItem> Downloads => _items.ToList();

        public event EventHandler<DownloadListChangedEventArgs>? DownloadListChanged;

        // Part of the interface; QueueManager subscribes to none of them.
#pragma warning disable CS0067
        public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
        public event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;
        public event EventHandler<ConnectionsSnapshotEventArgs>? ConnectionsChanged;
#pragma warning restore CS0067

        public async Task ResumeAsync(Guid downloadId)
        {
            Resumed.Add(downloadId);
            var item = _items.FirstOrDefault(i => i.Id == downloadId);

            if (BlockResume)
            {
                if (item is not null) item.Status = DownloadStatus.Downloading;
                await _release.Task.ConfigureAwait(false);
            }

            if (item is not null)
                item.Status = LeaveQueuedAfterResume ? DownloadStatus.Queued : DownloadStatus.Completed;
        }

        public void Pause(Guid downloadId)
        {
            Paused.Add(downloadId);
            var item = _items.FirstOrDefault(i => i.Id == downloadId);
            if (item is not null) item.Status = DownloadStatus.Paused;
        }

        public void Stop(Guid downloadId) => Stopped.Add(downloadId);

        public void SetQueueSpeedLimit(Guid downloadId, long bytesPerSecond) =>
            QueueSpeedLimits.Add((downloadId, bytesPerSecond));

        // Not exercised by these tests, but part of the interface.
        public Task<DownloadItem> AddDownloadAsync(DownloadRequest request) => throw new NotSupportedException();
        public IDownloadService? GetService(Guid downloadId) => null;
        public Task StartAsync(Guid downloadId) => ResumeAsync(downloadId);
        public int PauseAll() => 0;
        public Task RetryAsync(Guid downloadId) => ResumeAsync(downloadId);
        public void Remove(Guid downloadId, bool deleteFile) { }
        public long GlobalSpeedLimitBytesPerSecond => 0;
        public void SetSpeedLimit(Guid downloadId, long bytesPerSecond) { }
        public void SetGlobalSpeedLimit(long bytesPerSecond) { }
        public void LoadPersistedDownloads() { }
        public Task<int> CleanupOrphanedTempFoldersAsync() => Task.FromResult(0);
    }
}
