using QuickByte.Core.Enums;
using QuickByte.Core.Events;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.Core.Services;

namespace QuickByte.Core.Tests.Services;

/// <summary>
/// The facade every window talks to. Most of these run the real pool, the real
/// service and the real merger over a fake connection factory, so the wiring is
/// exercised rather than mocked out — including the end-to-end case where eight
/// segments become one file.
/// </summary>
public sealed class DownloadManagerTests
{
    [Fact]
    public void Anything_persisted_as_in_flight_comes_back_paused()
    {
        // Otherwise the list shows downloads as running that no longer are.
        using var fixture = new Fixture();
        fixture.Repository.Items.AddRange(new[]
        {
            new DownloadItem { Status = DownloadStatus.Downloading },
            new DownloadItem { Status = DownloadStatus.Connecting },
            new DownloadItem { Status = DownloadStatus.Merging },
            new DownloadItem { Status = DownloadStatus.Completed },
            new DownloadItem { Status = DownloadStatus.Failed }
        });

        fixture.Manager.LoadPersistedDownloads();

        var byStatus = fixture.Manager.Downloads.GroupBy(i => i.Status).ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(3, byStatus[DownloadStatus.Paused]);
        Assert.Equal(1, byStatus[DownloadStatus.Completed]);
        Assert.Equal(1, byStatus[DownloadStatus.Failed]);
    }

    [Fact]
    public async Task Adding_a_download_sanitizes_the_name_the_server_gave()
    {
        using var fixture = new Fixture();

        var item = await fixture.AddAsync(fileName: "../../autorun.inf");

        Assert.Equal("autorun.inf", item.FileName);
    }

    [Fact]
    public async Task Adding_a_download_avoids_overwriting_a_file_already_there()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Downloads, "a.bin"), "existing");

        var item = await fixture.AddAsync(fileName: "a.bin");

        Assert.Equal("a (1).bin", item.FileName);
    }

    [Fact]
    public async Task Adding_a_download_clamps_the_connection_count()
    {
        using var fixture = new Fixture();

        var item = await fixture.AddAsync(connections: 500);

        Assert.Equal(DownloadSettings.MaxConnections, item.ConnectionsCount);
    }

    [Fact]
    public async Task A_download_destined_for_a_queue_is_added_and_left_alone()
    {
        // Starting it here would be the one thing the user asked not to happen by
        // choosing a queue at all.
        using var fixture = new Fixture();

        var item = await fixture.AddAsync(startImmediately: false);

        Assert.Equal(DownloadStatus.Queued, item.Status);
        Assert.Equal(0, fixture.Factory.Created);
    }

    [Fact]
    public async Task Adding_a_download_claims_a_temp_folder_of_its_own()
    {
        using var fixture = new Fixture();

        var item = await fixture.AddAsync(startImmediately: false);

        Assert.StartsWith(fixture.Temp, item.TempFolderPath);
        Assert.True(Guid.TryParseExact(Path.GetFileName(item.TempFolderPath), "N", out _));
    }

    [Fact]
    public async Task The_credentials_are_copied_rather_than_shared_with_the_request()
    {
        using var fixture = new Fixture();
        var credentials = new DownloadCredentials { UserName = "alice", Password = "hunter2" };

        var item = await fixture.AddAsync(startImmediately: false, credentials: credentials);

        Assert.NotSame(credentials, item.Credentials);
        Assert.Equal("alice", item.Credentials!.UserName);
        Assert.Equal("hunter2", item.Credentials.Password);
    }

    [Fact]
    public async Task The_captured_headers_are_stored_case_insensitively()
    {
        using var fixture = new Fixture();

        var item = await fixture.AddAsync(
            startImmediately: false,
            headers: new Dictionary<string, string> { ["Cookie"] = "session=abc" });

        Assert.Equal("session=abc", item.Headers["cookie"]);
    }

    [Fact]
    public async Task Adding_and_removing_a_download_is_announced()
    {
        using var fixture = new Fixture();
        var changes = new List<DownloadListChangeType>();
        fixture.Manager.DownloadListChanged += (_, e) => changes.Add(e.ChangeType);

        var item = await fixture.AddAsync(startImmediately: false);
        fixture.Manager.Remove(item.Id, deleteFile: false);

        Assert.Equal(new[] { DownloadListChangeType.Added, DownloadListChangeType.Removed }, changes);
        Assert.Empty(fixture.Manager.Downloads);
    }

    [Fact]
    public async Task Removing_a_download_can_take_its_file_with_it()
    {
        using var fixture = new Fixture();
        var item = await fixture.AddAsync(startImmediately: false, fileName: "a.bin");
        File.WriteAllText(item.FullPath, "content");

        fixture.Manager.Remove(item.Id, deleteFile: true);

        Assert.False(File.Exists(item.FullPath));
    }

    [Fact]
    public async Task Removing_a_download_can_leave_its_file_behind()
    {
        using var fixture = new Fixture();
        var item = await fixture.AddAsync(startImmediately: false, fileName: "a.bin");
        File.WriteAllText(item.FullPath, "content");

        fixture.Manager.Remove(item.Id, deleteFile: false);

        Assert.True(File.Exists(item.FullPath));
    }

    [Fact]
    public void Removing_something_that_is_not_there_is_harmless()
    {
        using var fixture = new Fixture();

        fixture.Manager.Remove(Guid.NewGuid(), deleteFile: true);
    }

    [Fact]
    public async Task A_speed_limit_set_on_a_download_is_persisted()
    {
        // So a limit set on a big download survives a pause or a restart rather
        // than quietly reverting to full speed.
        using var fixture = new Fixture();
        var item = await fixture.AddAsync(startImmediately: false);

        fixture.Manager.SetSpeedLimit(item.Id, 250_000);

        Assert.Equal(250_000, item.SpeedLimitBytesPerSecond);
        Assert.Equal(250_000, fixture.Repository.LastSaved.Single().SpeedLimitBytesPerSecond);
    }

    [Fact]
    public async Task A_negative_speed_limit_is_read_as_unlimited()
    {
        using var fixture = new Fixture();
        var item = await fixture.AddAsync(startImmediately: false);

        fixture.Manager.SetSpeedLimit(item.Id, -1);

        Assert.Equal(0, item.SpeedLimitBytesPerSecond);
    }

    [Fact]
    public void The_global_speed_limit_follows_the_settings_without_a_restart()
    {
        // The one setting honoured live: a limit you have to restart the app to
        // apply is one the user will assume is broken.
        using var fixture = new Fixture();

        fixture.Settings.Save(new DownloadSettings
        {
            GlobalSpeedLimitBytesPerSecond = 512_000,
            DefaultDownloadFolder = fixture.Downloads,
            TempFolder = fixture.Temp
        });

        Assert.Equal(512_000, fixture.Manager.GlobalSpeedLimitBytesPerSecond);
    }

    [Fact]
    public void The_global_speed_limit_can_also_be_set_directly()
    {
        using var fixture = new Fixture();

        fixture.Manager.SetGlobalSpeedLimit(64_000);

        Assert.Equal(64_000, fixture.Manager.GlobalSpeedLimitBytesPerSecond);
    }

    [Fact]
    public void PauseAll_counts_only_what_was_actually_in_flight()
    {
        using var fixture = new Fixture();
        fixture.Repository.Items.AddRange(new[]
        {
            new DownloadItem { Status = DownloadStatus.Completed },
            new DownloadItem { Status = DownloadStatus.Queued },
            new DownloadItem { Status = DownloadStatus.Paused }
        });
        fixture.Manager.LoadPersistedDownloads();

        // Set after the load, which downgrades anything persisted as in-flight —
        // this is the shutdown case, where downloads really are running.
        fixture.Repository.Items[1].Status = DownloadStatus.Downloading;
        fixture.Repository.Items[2].Status = DownloadStatus.Connecting;

        // The count is what lets shutdown know whether it has anything to wait on.
        Assert.Equal(2, fixture.Manager.PauseAll());
        Assert.All(fixture.Manager.Downloads, item => Assert.False(DownloadActions.CanPause(item)));
    }

    // --------------------------------------------------- orphaned temp folders --

    [Fact]
    public async Task The_sweep_deletes_a_folder_no_live_download_claims()
    {
        using var fixture = new Fixture();
        string orphan = Path.Combine(fixture.Temp, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(orphan);

        Assert.Equal(1, await fixture.Manager.CleanupOrphanedTempFoldersAsync());
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public async Task The_sweep_leaves_a_folder_a_paused_download_is_resuming_from()
    {
        using var fixture = new Fixture();
        var item = await fixture.AddAsync(startImmediately: false);
        item.Status = DownloadStatus.Paused;
        Directory.CreateDirectory(item.TempFolderPath);

        Assert.Equal(0, await fixture.Manager.CleanupOrphanedTempFoldersAsync());
        Assert.True(Directory.Exists(item.TempFolderPath));
    }

    [Fact]
    public async Task The_sweep_collects_a_cancelled_download_s_folder()
    {
        // Stop() means discard, unlike Pause(). The item only still carries the
        // path because the delete lost the race with the connections closing.
        using var fixture = new Fixture();
        var item = await fixture.AddAsync(startImmediately: false);
        item.Status = DownloadStatus.Cancelled;
        Directory.CreateDirectory(item.TempFolderPath);

        Assert.Equal(1, await fixture.Manager.CleanupOrphanedTempFoldersAsync());
        Assert.False(Directory.Exists(item.TempFolderPath));
    }

    [Fact]
    public async Task The_sweep_leaves_alone_anything_this_app_did_not_name()
    {
        // TempFolder is user-configurable and may well point at a directory
        // shared with other programs.
        using var fixture = new Fixture();
        string somebodyElses = Path.Combine(fixture.Temp, "SomeOtherProgram");
        Directory.CreateDirectory(somebodyElses);

        Assert.Equal(0, await fixture.Manager.CleanupOrphanedTempFoldersAsync());
        Assert.True(Directory.Exists(somebodyElses));
    }

    // ------------------------------------------------------------ end to end --

    [Fact]
    public async Task A_download_runs_through_the_pool_and_the_merger_into_one_file()
    {
        // The real ConnectionPoolManager, the real DownloadService and the real
        // FileMerger over fake connections that write actual bytes: the split, the
        // chunk files, the ordering and the merge, all the way to the file on disk.
        using var fixture = new Fixture();
        byte[] content = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        fixture.Factory.Content = content;
        fixture.Info.ContentLength = content.Length;
        fixture.Info.SupportsRangeRequests = true;

        var item = await fixture.AddAsync(fileName: "whole.bin", connections: 8, startImmediately: true);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(content, File.ReadAllBytes(item.FullPath));
        Assert.Equal(8, fixture.Factory.Created);
        Assert.False(Directory.Exists(item.TempFolderPath));
    }

    [Fact]
    public async Task A_download_that_is_interrupted_resumes_from_its_chunks()
    {
        using var fixture = new Fixture();
        byte[] content = Enumerable.Range(0, 4000).Select(i => (byte)(i % 251)).ToArray();
        fixture.Factory.Content = content;
        fixture.Info.ContentLength = content.Length;
        fixture.Info.SupportsRangeRequests = true;

        // First pass: every connection writes only half of its range.
        fixture.Factory.WriteFraction = 0.5;
        var item = await fixture.AddAsync(fileName: "resumed.bin", connections: 4, startImmediately: true);
        Assert.Equal(DownloadStatus.Failed, item.Status);

        // Second pass: the rest arrives, starting from what is already on disk.
        fixture.Factory.WriteFraction = 1.0;
        await fixture.Manager.RetryAsync(item.Id);

        Assert.Equal(DownloadStatus.Completed, item.Status);
        Assert.Equal(content, File.ReadAllBytes(item.FullPath));
        Assert.Contains(fixture.Factory.ResumeOffsets, offset => offset > 0);
    }

    // ------------------------------------------------------------- plumbing --

    private sealed class Fixture : IDisposable
    {
        private readonly TempFolder _root = new();

        public Fixture()
        {
            Downloads = Path.Combine(_root.Path, "downloads");
            Temp = Path.Combine(_root.Path, "temp");
            Directory.CreateDirectory(Downloads);
            Directory.CreateDirectory(Temp);

            Settings = new RecordingSettings(new DownloadSettings
            {
                DefaultDownloadFolder = Downloads,
                TempFolder = Temp,
                MaxRetries = 0,
                RetryDelayMilliseconds = 0
            });
            Repository = new FakeRepository();
            Info = new RemoteFileInfo { ContentLength = 1000, ContentType = "application/octet-stream" };
            Factory = new ChunkWritingConnectionFactory();

            Manager = new DownloadManager(
                Repository, Settings, new FakeInfoProvider(Info), Factory, new FileMerger(), new InlineDispatcher());
        }

        public string Downloads { get; }
        public string Temp { get; }
        public RecordingSettings Settings { get; }
        public FakeRepository Repository { get; }
        public RemoteFileInfo Info { get; }
        public ChunkWritingConnectionFactory Factory { get; }
        public DownloadManager Manager { get; }

        public Task<DownloadItem> AddAsync(
            string fileName = "file.bin",
            int connections = 4,
            bool startImmediately = false,
            DownloadCredentials? credentials = null,
            IReadOnlyDictionary<string, string>? headers = null) =>
            Manager.AddDownloadAsync(new DownloadRequest(
                "https://example.com/file.bin", Info, Downloads, fileName, connections)
            {
                StartImmediately = startImmediately,
                Credentials = credentials,
                Headers = headers
            });

        public void Dispose() => _root.Dispose();
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class RecordingSettings : ISettingsService
    {
        public RecordingSettings(DownloadSettings settings) => Current = settings;

        public DownloadSettings Current { get; private set; }
        public event EventHandler<DownloadSettings>? SettingsChanged;

        public void Load() { }

        public void Save(DownloadSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(this, settings);
        }
    }

    private sealed class FakeRepository : IDownloadRepository
    {
        public List<DownloadItem> Items { get; } = new();
        public List<DownloadItem> LastSaved { get; private set; } = new();

        public List<DownloadItem> LoadAll() => Items;

        public void SaveAll(IEnumerable<DownloadItem> items) => LastSaved = items.ToList();
    }

    private sealed class FakeInfoProvider : IRemoteFileInfoProvider
    {
        private readonly RemoteFileInfo _info;

        public FakeInfoProvider(RemoteFileInfo info) => _info = info;

        public Task<RemoteFileInfo> GetFileInfoAsync(
            string url, RequestOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(_info);
    }

    /// <summary>
    /// A connection that writes its own slice of a known payload into its chunk
    /// file, so the pool, the resume offsets and the merge can be checked against
    /// real bytes without a server.
    /// </summary>
    private sealed class ChunkWritingConnectionFactory : IConnectionFactory
    {
        public byte[] Content { get; set; } = Array.Empty<byte>();

        /// <summary>How much of each range to write; below 1.0 leaves the segment short.</summary>
        public double WriteFraction { get; set; } = 1.0;

        public int Created { get; private set; }
        public List<long> ResumeOffsets { get; } = new();

        public IDownloadConnection Create(
            int connectionId, string url, long rangeStart, long rangeEnd, long alreadyDownloaded,
            string chunkFilePath, DownloadSettings settings,
            IBandwidthLimiter? bandwidthLimiter = null, RequestOptions? options = null)
        {
            Created++;
            ResumeOffsets.Add(alreadyDownloaded);
            return new ChunkWritingConnection(
                connectionId, rangeStart, rangeEnd, alreadyDownloaded, chunkFilePath, Content, WriteFraction);
        }
    }

    private sealed class ChunkWritingConnection : IDownloadConnection
    {
        private readonly byte[] _content;
        private readonly double _fraction;

        public ChunkWritingConnection(
            int connectionId, long rangeStart, long rangeEnd, long alreadyDownloaded,
            string chunkFilePath, byte[] content, double fraction)
        {
            ConnectionId = connectionId;
            RangeStart = rangeStart;
            RangeEnd = rangeEnd;
            ChunkFilePath = chunkFilePath;
            BytesDownloaded = alreadyDownloaded;
            _content = content;
            _fraction = fraction;
        }

        public int ConnectionId { get; }
        public long RangeStart { get; }
        public long RangeEnd { get; }
        public long BytesDownloaded { get; private set; }
        public int RetryCount => 0;
        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Idle;
        public string? LastError => null;
        public string ChunkFilePath { get; }

        private long RangeLength => RangeEnd - RangeStart + 1;

        public Task RunAsync(CancellationToken cancellationToken)
        {
            long target = (long)(RangeLength * _fraction);
            if (BytesDownloaded >= target)
            {
                Status = BytesDownloaded >= RangeLength ? ConnectionStatus.Finished : ConnectionStatus.Failed;
                return Task.CompletedTask;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(ChunkFilePath)!);
            using (var stream = new FileStream(ChunkFilePath, FileMode.OpenOrCreate, FileAccess.Write))
            {
                stream.Seek(BytesDownloaded, SeekOrigin.Begin);
                long from = RangeStart + BytesDownloaded;
                long count = target - BytesDownloaded;
                stream.Write(_content, (int)from, (int)count);
                BytesDownloaded = target;
            }

            Status = BytesDownloaded >= RangeLength ? ConnectionStatus.Finished : ConnectionStatus.Failed;
            return Task.CompletedTask;
        }
    }
}
