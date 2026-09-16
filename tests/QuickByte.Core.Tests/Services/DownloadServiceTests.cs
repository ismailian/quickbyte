using QuickByte.Core.Enums;
using QuickByte.Core.Events;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;
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

    // ------------------------------------------- a file replaced on the server --

    [Fact]
    public async Task A_resume_throws_away_the_chunks_of_a_file_the_server_has_since_replaced()
    {
        // The corruption this exists for: chunks of the old version and the tail
        // of the new one, merged into a file that is neither, with no error.
        using var fixture = new Fixture();
        fixture.Item.TotalBytes = 1000;
        fixture.Item.DownloadedBytes = 400;
        fixture.Item.RemoteVersion = Version("\"v1\"");
        fixture.Info.ETag = "\"v2\"";

        await fixture.Service.StartAsync();

        Assert.Equal(1, fixture.Pool.Discards);
        Assert.Equal("\"v2\"", fixture.Item.RemoteVersion!.ETag);
        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
    }

    [Fact]
    public async Task A_resume_of_the_version_already_on_disk_keeps_every_chunk()
    {
        using var fixture = new Fixture();
        fixture.Item.TotalBytes = 1000;
        fixture.Item.RemoteVersion = Version("\"v1\"");
        fixture.Info.ETag = "\"v1\"";

        await fixture.Service.StartAsync();

        Assert.Equal(0, fixture.Pool.Discards);
    }

    [Fact]
    public async Task A_file_that_changed_length_is_a_different_file_even_without_validators()
    {
        // A length change also moves every segment boundary, so the old chunks
        // would resume at offsets that no longer belong to them.
        using var fixture = new Fixture();
        fixture.Item.TotalBytes = 900;
        fixture.Info.ContentLength = 1000;

        await fixture.Service.StartAsync();

        Assert.Equal(1, fixture.Pool.Discards);
        Assert.Equal(1000, fixture.Item.TotalBytes);
    }

    [Fact]
    public async Task A_download_recorded_before_versions_were_kept_adopts_one_without_losing_anything()
    {
        using var fixture = new Fixture();
        fixture.Item.TotalBytes = 1000;
        fixture.Item.RemoteVersion = null;
        fixture.Info.ETag = "\"v1\"";

        await fixture.Service.StartAsync();

        Assert.Equal(0, fixture.Pool.Discards);
        Assert.Equal("\"v1\"", fixture.Item.RemoteVersion!.ETag);
    }

    [Fact]
    public async Task A_change_a_connection_reports_is_confirmed_by_a_probe_before_the_chunks_go()
    {
        using var fixture = new Fixture();
        fixture.Pool.ThrowChangedTimes = 1;
        fixture.Provider.Respond = probe => Info(probe == 1 ? "\"v1\"" : "\"v2\"");

        await fixture.Service.StartAsync();

        Assert.Equal(2, fixture.Provider.Probes);
        Assert.Equal(1, fixture.Pool.Discards);
        Assert.False(fixture.Item.IfRangeUnsupported);
        Assert.Equal("\"v2\"", fixture.Item.RemoteVersion!.ETag);
        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
    }

    [Fact]
    public async Task A_change_the_probe_contradicts_is_the_condition_s_fault_and_costs_no_chunks()
    {
        // A server that does not evaluate If-Range, or whose edges mint their own
        // ETags, answers with a whole file for a version that never changed.
        // Discarding on its word would cost hours for nothing.
        using var fixture = new Fixture();
        fixture.Pool.ThrowChangedTimes = 1;
        fixture.Provider.Respond = _ => Info("\"v1\"");

        await fixture.Service.StartAsync();

        Assert.Equal(0, fixture.Pool.Discards);
        Assert.True(fixture.Item.IfRangeUnsupported);
        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
    }

    [Fact]
    public async Task A_file_that_never_holds_still_fails_instead_of_restarting_forever()
    {
        using var fixture = new Fixture();
        fixture.Pool.ThrowChangedTimes = int.MaxValue;
        fixture.Provider.Respond = probe => Info($"\"v{probe}\"");

        await fixture.Service.StartAsync();

        Assert.Equal(DownloadStatus.Failed, fixture.Item.Status);
        Assert.Contains("kept changing", fixture.Item.ErrorMessage);
    }

    [Fact]
    public async Task A_pause_during_the_confirming_probe_is_a_pause()
    {
        using var fixture = new Fixture();
        fixture.Pool.ThrowChangedTimes = 1;
        fixture.Provider.Respond = probe =>
        {
            if (probe == 2) fixture.Service.Pause();
            return Info("\"v1\"");
        };

        await fixture.Service.StartAsync();

        Assert.Equal(DownloadStatus.Paused, fixture.Item.Status);
    }

    // ------------------------------------------------------------ disk space --

    [Fact]
    public async Task A_download_that_will_not_fit_fails_before_its_first_byte_and_says_where()
    {
        // It used to fill the drive for hours and die in the merge at 100%.
        using var fixture = new Fixture(availableBytes: _ => 999);
        fixture.Info.ContentLength = 1000;

        await fixture.Service.StartAsync();

        Assert.Equal(DownloadStatus.Failed, fixture.Item.Status);
        Assert.Contains("Not enough free space", fixture.Item.ErrorMessage);
        Assert.Equal(0, fixture.Pool.Runs);
    }

    [Fact]
    public async Task What_is_already_downloaded_does_not_need_room_again()
    {
        using var fixture = new Fixture(availableBytes: _ => 700);
        fixture.Info.ContentLength = 1000;
        fixture.Item.TotalBytes = 1000;
        fixture.Item.DownloadedBytes = 400;

        await fixture.Service.StartAsync();

        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
    }

    [Fact]
    public async Task A_drive_that_cannot_be_measured_is_not_a_reason_to_refuse()
    {
        using var fixture = new Fixture(availableBytes: _ => null);

        await fixture.Service.StartAsync();

        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
    }

    // ---------------------------------------------------- an interrupted merge --

    [Fact]
    public async Task A_merge_interrupted_by_a_restart_finishes_without_asking_the_server()
    {
        // Every byte is already on disk. Probing first would fail a finished
        // download over a link that has since expired, and running the pool
        // would misread chunks the merge has already folded together.
        using var fixture = new Fixture();
        fixture.Item.Status = DownloadStatus.Paused;
        fixture.Merger.Interrupted = true;
        fixture.Provider.Throw = new HttpRequestException("403 (Forbidden)");

        await fixture.Service.StartAsync();

        Assert.Equal(DownloadStatus.Completed, fixture.Item.Status);
        Assert.Equal(0, fixture.Provider.Probes);
        Assert.Equal(0, fixture.Pool.Runs);
        Assert.Empty(Assert.Single(fixture.Merger.MergedChunkLists));
    }

    [Fact]
    public void Stopping_a_download_discards_an_interrupted_merge_with_it()
    {
        using var fixture = new Fixture();
        fixture.Item.Status = DownloadStatus.Paused;
        fixture.Merger.Interrupted = true;

        fixture.Service.Stop();

        Assert.Equal(1, fixture.Merger.Discards);
    }

    // --------------------------------------------------- the mark of the web --

    [Fact]
    public async Task A_finished_file_is_marked_as_coming_from_the_internet_and_the_page_it_came_from()
    {
        // A download handed over from Chrome used to arrive with fewer
        // protections than letting Chrome save it: no Zone.Identifier, so no
        // SmartScreen check and no Protected View.
        using var fixture = new Fixture();
        fixture.Merger.WriteDestination = true;
        fixture.Item.Headers = new Dictionary<string, string> { ["referer"] = "https://example.com/downloads" };

        await fixture.Service.StartAsync();

        string stream = File.ReadAllText($"{fixture.Item.FullPath}:{MarkOfTheWeb.StreamName}");
        Assert.Contains("ZoneId=3", stream);
        Assert.Contains("HostUrl=https://example.com/file.bin", stream);
        Assert.Contains("ReferrerUrl=https://example.com/downloads", stream);
    }

    [Fact]
    public async Task No_mark_is_written_where_policy_says_to_keep_no_zone_information()
    {
        using var fixture = new Fixture(markFromInternet: false);
        fixture.Merger.WriteDestination = true;

        await fixture.Service.StartAsync();

        Assert.Throws<FileNotFoundException>(() => File.ReadAllText($"{fixture.Item.FullPath}:{MarkOfTheWeb.StreamName}"));
    }

    // ------------------------------------------------------ a new address --

    [Fact]
    public void A_new_address_keeps_the_bytes_already_on_disk()
    {
        // An expired signed link: the file is the same file, only the permission
        // to ask for it ran out.
        using var fixture = new Fixture();
        fixture.Item.Status = DownloadStatus.Failed;
        fixture.Item.ErrorMessage = "Response status code does not indicate success: 403 (Forbidden).";
        fixture.Item.DownloadedBytes = 400;

        bool changed = fixture.Service.ChangeAddress(new AddressChange("https://cdn.example.com/file.bin?sig=new", Info("\"v1\"")));

        Assert.True(changed);
        Assert.Equal("https://cdn.example.com/file.bin?sig=new", fixture.Item.Url);
        Assert.Equal(0, fixture.Pool.Discards);
        Assert.Equal(400, fixture.Item.DownloadedBytes);
        Assert.Null(fixture.Item.ErrorMessage);
        Assert.Equal(DownloadStatus.Failed, fixture.Item.Status);
    }

    [Fact]
    public void A_new_address_the_user_chose_to_start_over_with_discards_the_chunks()
    {
        using var fixture = new Fixture();
        fixture.Item.Status = DownloadStatus.Paused;
        fixture.Item.DownloadedBytes = 400;

        fixture.Service.ChangeAddress(new AddressChange("https://example.com/v2.bin", Info("\"v2\"")) { DiscardProgress = true });

        Assert.Equal(1, fixture.Pool.Discards);
        Assert.Equal(0, fixture.Item.DownloadedBytes);
    }

    [Fact]
    public void A_new_address_brings_its_own_login_and_headers_or_keeps_the_old_ones()
    {
        using var fixture = new Fixture();
        fixture.Item.Status = DownloadStatus.Paused;
        fixture.Item.Credentials = new DownloadCredentials { UserName = "alice", Password = "old" };
        fixture.Item.Headers["Cookie"] = "session=expired";

        fixture.Service.ChangeAddress(new AddressChange("https://example.com/a", Info("\"v1\"")));
        Assert.Equal("old", fixture.Item.Credentials!.Password);
        Assert.Equal("session=expired", fixture.Item.Headers["Cookie"]);

        fixture.Service.ChangeAddress(new AddressChange("https://example.com/b", Info("\"v1\""))
        {
            Credentials = new DownloadCredentials { UserName = "alice", Password = "new" },
            Headers = new Dictionary<string, string> { ["Cookie"] = "session=fresh" }
        });
        Assert.Equal("new", fixture.Item.Credentials!.Password);
        Assert.Equal("session=fresh", fixture.Item.Headers["cookie"]);
    }

    [Fact]
    public void A_new_server_is_not_held_to_the_old_one_s_broken_if_range()
    {
        using var fixture = new Fixture();
        fixture.Item.Status = DownloadStatus.Paused;
        fixture.Item.IfRangeUnsupported = true;

        var probed = Info("\"v1\"");
        probed.FinalUrl = "https://other.example.com/file.bin";
        fixture.Service.ChangeAddress(new AddressChange("https://other.example.com/file.bin", probed));

        Assert.False(fixture.Item.IfRangeUnsupported);
        Assert.Equal("other.example.com", fixture.Item.RemoteVersion!.Host);
    }

    [Fact]
    public async Task The_address_of_a_running_download_cannot_be_changed_under_its_connections()
    {
        using var fixture = new Fixture();
        fixture.Pool.BlockUntilCancelled = true;
        var running = fixture.Service.StartAsync();
        await fixture.Pool.Started.Task;

        bool changed = fixture.Service.ChangeAddress(new AddressChange("https://example.com/new", Info("\"v1\"")));

        Assert.False(changed);
        Assert.Equal("https://example.com/file.bin", fixture.Item.Url);

        fixture.Service.Pause();
        await running;
    }

    [Fact]
    public void A_finished_download_has_no_address_to_refresh()
    {
        using var fixture = new Fixture();
        fixture.Item.Status = DownloadStatus.Completed;

        Assert.False(fixture.Service.ChangeAddress(new AddressChange("https://example.com/new", Info("\"v1\""))));
    }

    [Fact]
    public async Task The_next_start_probes_the_new_address_rather_than_reusing_the_old_answer()
    {
        using var fixture = new Fixture();
        fixture.Pool.BlockUntilCancelled = true;
        var running = fixture.Service.StartAsync();
        await fixture.Pool.Started.Task;
        fixture.Service.Pause();
        await running;

        fixture.Service.ChangeAddress(new AddressChange("https://example.com/renewed", Info(null)));
        fixture.Pool.BlockUntilCancelled = false;
        await fixture.Service.ResumeAsync();

        Assert.Equal(2, fixture.Provider.Probes);
        Assert.Equal("https://example.com/renewed", fixture.Provider.LastUrl);
    }

    [Fact]
    public void Changing_the_address_is_announced_so_every_window_shows_it()
    {
        using var fixture = new Fixture();
        fixture.Item.Status = DownloadStatus.Paused;
        int persistedBefore = fixture.Persisted;

        fixture.Service.ChangeAddress(new AddressChange("https://example.com/new", Info("\"v1\"")));

        Assert.Equal(new[] { DownloadStatus.Paused }, fixture.StatusHistory);
        Assert.True(fixture.Persisted > persistedBefore);
    }

    private static RemoteFileVersion Version(string etag) => new() { ETag = etag, Host = "example.com" };

    private static RemoteFileInfo Info(string? etag) => new()
    {
        ContentLength = 1000,
        ContentType = "application/octet-stream",
        SupportsRangeRequests = true,
        FinalUrl = "https://example.com/file.bin",
        ETag = etag
    };

    // ------------------------------------------------------------- plumbing --

    private sealed class Fixture : IDisposable
    {
        private readonly TempFolder _folder = new();

        public Fixture(bool markFromInternet = true, Func<string, long?>? availableBytes = null)
        {
            Item = new DownloadItem
            {
                Url = "https://example.com/file.bin",
                FileName = "file.bin",
                SaveFolder = _folder.Path
            };
            Settings = new FakeSettings(new DownloadSettings { TempFolder = _folder.Path });
            Info = new RemoteFileInfo
            {
                ContentLength = 1000,
                ContentType = "application/octet-stream",
                FinalUrl = "https://example.com/file.bin"
            };
            Provider = new FakeInfoProvider(Info);
            Pool = new FakePool();
            Merger = new FakeMerger();

            Service = new DownloadService(
                Item, Pool, Merger, Provider, Settings, _ => Persisted++, markFromInternet, availableBytes ?? (_ => null));
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
        public string? LastUrl { get; private set; }
        public Exception? Throw { get; set; }

        /// <summary>Answers probe N (counting from 1) when set; otherwise every probe gets the fixture's info.</summary>
        public Func<int, RemoteFileInfo>? Respond { get; set; }

        public Task<RemoteFileInfo> GetFileInfoAsync(
            string url, RequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Probes++;
            LastUrl = url;
            if (Throw is not null) return Task.FromException<RemoteFileInfo>(Throw);
            return Task.FromResult(Respond?.Invoke(Probes) ?? _info);
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

        /// <summary>How many runs report that the file changed on the server before one completes.</summary>
        public int ThrowChangedTimes { get; set; }

        public int Discards { get; private set; }

        public void DiscardChunks(string tempFolder) => Discards++;

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

            if (ThrowChangedTimes > 0)
            {
                ThrowChangedTimes--;
                throw new RemoteFileChangedException("The file on the server has changed.");
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

        /// <summary>Leaves a real file at the destination, as a merge would.</summary>
        public bool WriteDestination { get; set; }

        /// <summary>Pretends a merge was interrupted in the temp folder, as a restart mid-merge leaves it.</summary>
        public bool Interrupted { get; set; }

        public List<IReadOnlyList<string>> MergedChunkLists { get; } = new();
        public int Discards { get; private set; }

        public bool HasInterruptedMerge(string tempFolder) => Interrupted;

        public void DiscardInterruptedMerge(string tempFolder, string destinationFilePath)
        {
            Interrupted = false;
            Discards++;
        }

        public Task MergeAsync(
            string tempFolder, IReadOnlyList<string> orderedChunkPaths, string destinationFilePath, int bufferSize,
            IProgress<long>? bytesMergedProgress, CancellationToken cancellationToken)
        {
            Merged = true;
            Interrupted = false;
            MergedChunkLists.Add(orderedChunkPaths);
            if (WriteDestination) File.WriteAllBytes(destinationFilePath, new byte[] { 1, 2, 3 });
            foreach (long merged in ReportDuringMerge) bytesMergedProgress?.Report(merged);
            return Task.CompletedTask;
        }

        public void CleanupChunks(IReadOnlyList<string> chunkPaths, string tempFolder) { }
    }
}
