using QuickByte.Core.Enums;
using QuickByte.Core.Events;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.Core.Services;

namespace QuickByte.Core.Tests.Services;

/// <summary>
/// One download's lifecycle, driven with a fake pool and merger so the state
/// machine can be exercised without a server or a stopwatch.
/// </summary>
public sealed class DownloadServiceTests
{
    [Fact]
    public async Task A_download_walks_from_connecting_to_completed()
    {
        using var fixture = new Fixture();

        await fixture.Service.StartAsync();

        Assert.Equal(
            new[]
            {
                DownloadStatus.Connecting, DownloadStatus.Downloading,
                DownloadStatus.Merging, DownloadStatus.Completed
            },
            fixture.StatusHistory);
        Assert.NotNull(fixture.Item.CompletedAt);
    }

    [Fact]
    public async Task The_metadata_resolved_up_front_lands_on_the_item()
    {
        using var fixture = new Fixture();
        fixture.Info.ContentLength = 4096;
        fixture.Info.ContentType = "application/zip";
        fixture.Info.SupportsRangeRequests = true;

        await fixture.Service.StartAsync();

        Assert.Equal(4096, fixture.Item.TotalBytes);
        Assert.Equal("application/zip", fixture.Item.ContentType);
        Assert.True(fixture.Item.SupportsResume);
    }

    [Fact]
    public async Task A_temp_folder_is_claimed_under_the_configured_root()
    {
        using var fixture = new Fixture();

        await fixture.Service.StartAsync();

        Assert.StartsWith(fixture.Settings.Current.TempFolder, fixture.Item.TempFolderPath);
        Assert.True(Guid.TryParseExact(Path.GetFileName(fixture.Item.TempFolderPath), "N", out _));
    }

    [Fact]
    public async Task Progress_is_pinned_at_the_total_for_the_whole_merge()
    {
        // Every byte is on disk before the merge starts. Routing merge progress
        // back through DownloadedBytes is what made the bar visibly rewind.
        using var fixture = new Fixture();
        fixture.Info.ContentLength = 1000;
        fixture.Merger.ReportDuringMerge = new long[] { 250, 500, 1000 };

        var seen = new List<(long Downloaded, double? Merge)>();
        fixture.Service.ProgressChanged += (_, e) => seen.Add((e.DownloadedBytes, e.MergePercentage));

        await fixture.Service.StartAsync();

        var mergeReports = seen.Where(s => s.Merge is not null).ToList();
        Assert.NotEmpty(mergeReports);
        Assert.All(mergeReports, r => Assert.Equal(1000, r.Downloaded));
        Assert.Equal(new double?[] { 25, 50, 100 }, mergeReports.Select(r => r.Merge).ToArray());
    }

    [Fact]
    public async Task A_pool_that_could_not_finish_reports_the_connection_s_own_error()
    {
        // "404 (Not Found)" is a message a user can act on; the generic line is
        // one they can only report.
        using var fixture = new Fixture();
        fixture.Pool.Succeed = false;
        fixture.Pool.FailureMessage = "Connection #3: 404 (Not Found)";

        await fixture.Service.StartAsync();

        Assert.Equal(DownloadStatus.Failed, fixture.Item.Status);
        Assert.Equal("Connection #3: 404 (Not Found)", fixture.Item.ErrorMessage);
    }

    [Fact]
    public async Task A_failure_with_nothing_recorded_still_says_something()
    {
        using var fixture = new Fixture();
        fixture.Pool.Succeed = false;

        await fixture.Service.StartAsync();

        Assert.Equal(DownloadStatus.Failed, fixture.Item.Status);
        Assert.False(string.IsNullOrWhiteSpace(fixture.Item.ErrorMessage));
    }

    [Fact]
    public async Task A_stale_error_from_a_previous_attempt_is_not_reported_again()
    {
        using var fixture = new Fixture();
        fixture.Pool.Succeed = false;
        fixture.Pool.FailureMessage = "Connection #3: the first thing that went wrong";
        await fixture.Service.StartAsync();

        fixture.Pool.FailureMessage = null;
        await fixture.Service.RetryAsync();

        Assert.DoesNotContain("the first thing", fixture.Item.ErrorMessage);
    }

    [Fact]
    public async Task A_failure_is_surfaced_rather_than_thrown_at_the_caller()
    {
        using var fixture = new Fixture();
        fixture.Provider.Throw = new IOException("the name does not resolve");

        await fixture.Service.StartAsync();

        Assert.Equal(DownloadStatus.Failed, fixture.Item.Status);
        Assert.Equal("the name does not resolve", fixture.Item.ErrorMessage);
    }

    [Fact]
    public async Task Starting_a_download_that_is_already_running_does_nothing()
    {
        using var fixture = new Fixture();
        fixture.Item.Status = DownloadStatus.Downloading;

        await fixture.Service.StartAsync();

        Assert.Equal(0, fixture.Pool.Runs);
    }

    [Fact]
    public async Task Pause_stops_the_transfer_and_leaves_the_chunks_alone()
    {
        using var fixture = new Fixture();
        fixture.Pool.BlockUntilCancelled = true;

        var running = fixture.Service.StartAsync();
        await fixture.Pool.Started.Task;
        fixture.Service.Pause();
        await running;

        Assert.Equal(DownloadStatus.Paused, fixture.Item.Status);
        Assert.False(fixture.Merger.Merged);
    }

    [Fact]
    public void Pause_is_a_no_op_for_something_not_in_flight()
    {
        using var fixture = new Fixture();
        fixture.Item.Status = DownloadStatus.Completed;

        fixture.Service.Pause();

        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
    }

    [Fact]
    public void Stop_discards_the_download()
    {
        using var fixture = new Fixture();
        fixture.Item.Status = DownloadStatus.Paused;

        fixture.Service.Stop();

        Assert.Equal(DownloadStatus.Cancelled, fixture.Item.Status);
    }

    [Fact]
    public async Task Stop_does_not_rewrite_a_download_that_has_already_finished()
    {
        // DownloadManager calls Stop() on its way through Remove(), so removing a
        // completed download used to persist it as cancelled and announce that
        // status to every open window a moment before the row disappeared.
        using var fixture = new Fixture();
        await fixture.Service.StartAsync();
        fixture.StatusHistory.Clear();

        fixture.Service.Stop();

        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
        Assert.Empty(fixture.StatusHistory);
    }

    [Fact]
    public async Task Retry_re_resolves_the_file_in_case_the_server_side_changed()
    {
        using var fixture = new Fixture();
        fixture.Pool.Succeed = false;
        await fixture.Service.StartAsync();
        Assert.Equal(1, fixture.Provider.Probes);

        fixture.Pool.Succeed = true;
        await fixture.Service.RetryAsync();

        Assert.Equal(2, fixture.Provider.Probes);
        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
    }

    [Fact]
    public async Task Resume_reuses_the_metadata_it_already_has()
    {
        using var fixture = new Fixture();
        fixture.Pool.BlockUntilCancelled = true;
        var running = fixture.Service.StartAsync();
        await fixture.Pool.Started.Task;
        fixture.Service.Pause();
        await running;

        fixture.Pool.BlockUntilCancelled = false;
        await fixture.Service.ResumeAsync();

        // One probe, not two: the file was resolved when the download started.
        Assert.Equal(1, fixture.Provider.Probes);
        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
    }

    [Fact]
    public async Task Every_status_change_is_persisted()
    {
        using var fixture = new Fixture();

        await fixture.Service.StartAsync();

        Assert.Equal(fixture.StatusHistory.Count, fixture.Persisted);
    }

    [Fact]
    public async Task Repeated_pauses_and_resumes_do_not_pile_up_cancellation_sources()
    {
        // Each run used to leak the previous run's CancellationTokenSource.
        using var fixture = new Fixture();

        for (int i = 0; i < 5; i++)
        {
            fixture.Pool.BlockUntilCancelled = true;
            fixture.Pool.Started = new TaskCompletionSource();
            var running = fixture.Service.StartAsync();
            await fixture.Pool.Started.Task;
            fixture.Service.Pause();
            await running;
        }

        fixture.Pool.BlockUntilCancelled = false;
        await fixture.Service.ResumeAsync();

        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
    }

    // ------------------------------------------------------------- plumbing --

    private sealed class Fixture : IDisposable
    {
        private readonly TempFolder _folder = new();

        public Fixture()
        {
            Item = new DownloadItem
            {
                Url = "https://example.com/file.bin",
                FileName = "file.bin",
                SaveFolder = _folder.Path
            };
            Settings = new FakeSettings(new DownloadSettings { TempFolder = _folder.Path });
            Info = new RemoteFileInfo { ContentLength = 1000, ContentType = "application/octet-stream" };
            Provider = new FakeInfoProvider(Info);
            Pool = new FakePool();
            Merger = new FakeMerger();

            Service = new DownloadService(Item, Pool, Merger, Provider, Settings, _ => Persisted++);
            Service.StatusChanged += (_, e) => StatusHistory.Add(e.NewStatus);
        }

        public DownloadItem Item { get; }
        public RemoteFileInfo Info { get; }
        public FakeSettings Settings { get; }
        public FakeInfoProvider Provider { get; }
        public FakePool Pool { get; }
        public FakeMerger Merger { get; }
        public DownloadService Service { get; }
        public List<DownloadStatus> StatusHistory { get; } = new();
        public int Persisted { get; private set; }

        public void Dispose()
        {
            Service.Dispose();
            _folder.Dispose();
        }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(DownloadSettings settings) => Current = settings;

        public DownloadSettings Current { get; private set; }
        public event EventHandler<DownloadSettings>? SettingsChanged;

        public void Load() { }

        public void Save(DownloadSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(this, settings);
        }
    }

    private sealed class FakeInfoProvider : IRemoteFileInfoProvider
    {
        private readonly RemoteFileInfo _info;

        public FakeInfoProvider(RemoteFileInfo info) => _info = info;

        public int Probes { get; private set; }
        public Exception? Throw { get; set; }

        public Task<RemoteFileInfo> GetFileInfoAsync(
            string url, RequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Probes++;
            if (Throw is not null) return Task.FromException<RemoteFileInfo>(Throw);
            return Task.FromResult(_info);
        }
    }

    private sealed class FakePool : IConnectionPoolManager
    {
        public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
        public event EventHandler<ConnectionsSnapshotEventArgs>? ConnectionsChanged;
        public event EventHandler<string>? ConnectionFailed;

        public bool Succeed { get; set; } = true;
        public string? FailureMessage { get; set; }
        public bool BlockUntilCancelled { get; set; }
        public int Runs { get; private set; }
        public TaskCompletionSource Started { get; set; } = new();

        public IReadOnlyList<ConnectionInfo> Snapshot { get; } = Array.Empty<ConnectionInfo>();

        public async Task<bool> RunAsync(
            DownloadItem item, RemoteFileInfo fileInfo, DownloadSettings settings, CancellationToken cancellationToken)
        {
            Runs++;
            Started.TrySetResult();

            if (BlockUntilCancelled)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            if (!Succeed && FailureMessage is not null) ConnectionFailed?.Invoke(this, FailureMessage);

            // Keep the compiler from warning about events only this class raises.
            ConnectionsChanged?.Invoke(this, new ConnectionsSnapshotEventArgs
            {
                DownloadId = item.Id,
                Connections = Snapshot
            });
            ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
            {
                DownloadId = item.Id,
                DownloadedBytes = fileInfo.ContentLength,
                TotalBytes = fileInfo.ContentLength,
                SpeedBytesPerSecond = 0,
                EstimatedTimeRemaining = null
            });

            return Succeed;
        }

        public IReadOnlyList<string> GetOrderedChunkPaths() => new[] { "part0.tmp" };
    }

    private sealed class FakeMerger : IFileMerger
    {
        public bool Merged { get; private set; }
        public long[] ReportDuringMerge { get; set; } = Array.Empty<long>();

        public Task MergeAsync(
            IReadOnlyList<string> orderedChunkPaths, string destinationFilePath, int bufferSize,
            IProgress<long>? bytesMergedProgress, CancellationToken cancellationToken)
        {
            Merged = true;
            foreach (long merged in ReportDuringMerge) bytesMergedProgress?.Report(merged);
            return Task.CompletedTask;
        }

        public void CleanupChunks(IReadOnlyList<string> chunkPaths, string tempFolder) { }
    }
}
