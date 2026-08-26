using QuickByte.Core.Enums;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.Core.Services;

namespace QuickByte.Core.Tests.Services;

/// <summary>
/// The pool with fake connections, so the split, the resume offsets and the
/// chunk bookkeeping can be checked without a server.
/// </summary>
public sealed class ConnectionPoolManagerTests
{
    [Fact]
    public async Task A_server_that_supports_ranges_gets_the_download_s_connection_count()
    {
        using var folder = new TempFolder();
        var factory = new FakeConnectionFactory();
        var pool = new ConnectionPoolManager(factory);

        await pool.RunAsync(Item(folder, connections: 8), Info(1000, ranges: true), new DownloadSettings(), default);

        Assert.Equal(8, factory.Created.Count);
        Assert.Equal(0, factory.Created[0].RangeStart);
        Assert.Equal(999, factory.Created[^1].RangeEnd);
    }

    [Fact]
    public async Task A_server_without_range_support_drops_to_a_single_connection()
    {
        using var folder = new TempFolder();
        var factory = new FakeConnectionFactory();
        var pool = new ConnectionPoolManager(factory);

        await pool.RunAsync(Item(folder, connections: 8), Info(1000, ranges: false), new DownloadSettings(), default);

        var only = Assert.Single(factory.Created);
        Assert.Equal(0, only.RangeStart);
        Assert.Equal(999, only.RangeEnd);
    }

    [Fact]
    public async Task An_unknown_size_drops_to_a_single_open_ended_connection()
    {
        using var folder = new TempFolder();
        var factory = new FakeConnectionFactory();
        var pool = new ConnectionPoolManager(factory);

        await pool.RunAsync(Item(folder, connections: 8), Info(0, ranges: true), new DownloadSettings(), default);

        var only = Assert.Single(factory.Created);
        Assert.Equal(RangeSplitter.UnboundedEnd, only.RangeEnd);
    }

    [Fact]
    public async Task The_connection_count_is_clamped_by_the_application_settings()
    {
        using var folder = new TempFolder();
        var factory = new FakeConnectionFactory();
        var pool = new ConnectionPoolManager(factory);

        await pool.RunAsync(Item(folder, connections: 500), Info(100_000, ranges: true), new DownloadSettings(), default);

        Assert.Equal(DownloadSettings.MaxConnections, factory.Created.Count);
    }

    [Fact]
    public async Task Resume_offsets_come_from_the_length_of_the_chunks_on_disk()
    {
        // Not from a persisted counter: the bytes that are actually there are the
        // only thing that cannot be out of date.
        using var folder = new TempFolder();
        folder.WriteFile("part0.tmp", new byte[120]);
        folder.WriteFile("part2.tmp", new byte[45]);

        var factory = new FakeConnectionFactory();
        var pool = new ConnectionPoolManager(factory);

        await pool.RunAsync(Item(folder, connections: 4), Info(1000, ranges: true), new DownloadSettings(), default);

        Assert.Equal(120, factory.Created[0].AlreadyDownloaded);
        Assert.Equal(0, factory.Created[1].AlreadyDownloaded);
        Assert.Equal(45, factory.Created[2].AlreadyDownloaded);
    }

    [Fact]
    public async Task Chunks_from_a_wider_split_are_thrown_away()
    {
        // A Retry re-resolves the file, and a server that has stopped advertising
        // byte ranges moves the download from eight connections to one. part0.tmp
        // would then be read as the head of the whole file when it holds only the
        // first eighth of it, and part1-7 would be merged in from nowhere.
        using var folder = new TempFolder();
        for (int i = 0; i < 8; i++) folder.WriteFile($"part{i}.tmp", new byte[125]);

        var factory = new FakeConnectionFactory();
        var pool = new ConnectionPoolManager(factory);

        await pool.RunAsync(Item(folder, connections: 8), Info(1000, ranges: false), new DownloadSettings(), default);

        Assert.Equal(0, factory.Created.Single().AlreadyDownloaded);
        Assert.DoesNotContain(Directory.GetFiles(folder.Path, "part*.tmp"), p => new FileInfo(p).Length > 0);
    }

    [Fact]
    public async Task Chunks_from_a_narrower_split_are_thrown_away()
    {
        // The other direction: one connection's chunk holds a prefix of the whole
        // file, which is longer than the first range of the new eight-way split.
        using var folder = new TempFolder();
        folder.WriteFile("part0.tmp", new byte[700]);

        var factory = new FakeConnectionFactory();
        var pool = new ConnectionPoolManager(factory);

        await pool.RunAsync(Item(folder, connections: 8), Info(1000, ranges: true), new DownloadSettings(), default);

        Assert.All(factory.Created, c => Assert.Equal(0, c.AlreadyDownloaded));
    }

    [Fact]
    public async Task Chunks_from_the_same_split_are_kept()
    {
        // A run paused before every connection had opened its file leaves fewer
        // chunks than there are connections. That is an ordinary resume, not an
        // incompatible chunk set, and the bytes must survive it.
        using var folder = new TempFolder();
        folder.WriteFile("part0.tmp", new byte[50]);
        folder.WriteFile("part1.tmp", new byte[30]);

        var factory = new FakeConnectionFactory();
        var pool = new ConnectionPoolManager(factory);

        await pool.RunAsync(Item(folder, connections: 4), Info(1000, ranges: true), new DownloadSettings(), default);

        Assert.Equal(50, factory.Created[0].AlreadyDownloaded);
        Assert.Equal(30, factory.Created[1].AlreadyDownloaded);
        Assert.Equal(0, factory.Created[2].AlreadyDownloaded);
    }

    [Fact]
    public async Task GetOrderedChunkPaths_is_in_byte_order_whatever_order_the_connections_finished_in()
    {
        using var folder = new TempFolder();
        var pool = new ConnectionPoolManager(new FakeConnectionFactory());

        await pool.RunAsync(Item(folder, connections: 4), Info(1000, ranges: true), new DownloadSettings(), default);

        Assert.Equal(
            new[] { "part0.tmp", "part1.tmp", "part2.tmp", "part3.tmp" },
            pool.GetOrderedChunkPaths().Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task RunAsync_reports_success_only_when_every_connection_finished()
    {
        using var folder = new TempFolder();

        var allGood = new ConnectionPoolManager(new FakeConnectionFactory());
        Assert.True(await allGood.RunAsync(Item(folder, 4), Info(1000, true), new DownloadSettings(), default));

        using var second = new TempFolder();
        var oneFailed = new ConnectionPoolManager(new FakeConnectionFactory { FailConnection = 2 });
        Assert.False(await oneFailed.RunAsync(Item(second, 4), Info(1000, true), new DownloadSettings(), default));
    }

    [Fact]
    public async Task A_connection_that_throws_is_reported_rather_than_taking_the_pool_down()
    {
        using var folder = new TempFolder();
        var pool = new ConnectionPoolManager(new FakeConnectionFactory { FailConnection = 1 });
        string? reported = null;
        pool.ConnectionFailed += (_, message) => reported = message;

        await pool.RunAsync(Item(folder, 4), Info(1000, true), new DownloadSettings(), default);

        Assert.NotNull(reported);
        Assert.Contains("#1", reported);
    }

    [Fact]
    public async Task Progress_is_reported_with_a_final_exact_snapshot()
    {
        using var folder = new TempFolder();
        var pool = new ConnectionPoolManager(new FakeConnectionFactory());
        var progress = new List<long>();
        pool.ProgressChanged += (_, e) => progress.Add(e.DownloadedBytes);

        await pool.RunAsync(Item(folder, 4), Info(1000, true), new DownloadSettings(), default);

        Assert.NotEmpty(progress);
        Assert.Equal(1000, progress[^1]);
    }

    [Fact]
    public async Task The_snapshot_describes_every_connection()
    {
        using var folder = new TempFolder();
        var pool = new ConnectionPoolManager(new FakeConnectionFactory());

        await pool.RunAsync(Item(folder, 4), Info(1000, true), new DownloadSettings(), default);

        var snapshot = pool.Snapshot;
        Assert.Equal(4, snapshot.Count);
        Assert.All(snapshot, c => Assert.Equal(ConnectionStatus.Finished, c.Status));
        Assert.Equal(new[] { 0, 1, 2, 3 }, snapshot.Select(c => c.ConnectionId).ToArray());
    }

    [Fact]
    public async Task Every_connection_of_a_download_is_handed_the_same_request_options()
    {
        // A probe that authenticates and connections that do not resolve a real
        // size and then fetch eight copies of a login page.
        using var folder = new TempFolder();
        var item = Item(folder, connections: 4);
        item.Credentials = new DownloadCredentials { UserName = "alice", Password = "hunter2" };
        item.Headers["Cookie"] = "session=abc";

        var factory = new FakeConnectionFactory();
        await new ConnectionPoolManager(factory).RunAsync(item, Info(1000, true), new DownloadSettings(), default);

        Assert.All(factory.Created, c =>
        {
            Assert.Equal("alice", c.Options!.Credentials!.UserName);
            Assert.Equal("session=abc", c.Options.Headers!["Cookie"]);
        });
        Assert.Single(factory.Created.Select(c => c.Options).Distinct());
    }

    // ------------------------------------------------------------- plumbing --

    private static DownloadItem Item(TempFolder folder, int connections) => new()
    {
        Url = "https://example.com/file.bin",
        ConnectionsCount = connections,
        TempFolderPath = folder.Path
    };

    private static RemoteFileInfo Info(long length, bool ranges) => new()
    {
        ContentLength = length,
        SupportsRangeRequests = ranges
    };

    private sealed class FakeConnectionFactory : IConnectionFactory
    {
        public List<FakeConnection> Created { get; } = new();

        /// <summary>Id of a connection that should throw instead of transferring.</summary>
        public int? FailConnection { get; init; }

        public IDownloadConnection Create(
            int connectionId, string url, long rangeStart, long rangeEnd, long alreadyDownloaded,
            string chunkFilePath, DownloadSettings settings,
            IBandwidthLimiter? bandwidthLimiter = null, RequestOptions? options = null)
        {
            var connection = new FakeConnection(
                connectionId, rangeStart, rangeEnd, alreadyDownloaded, chunkFilePath, options,
                shouldFail: FailConnection == connectionId);
            Created.Add(connection);
            return connection;
        }
    }

    private sealed class FakeConnection : IDownloadConnection
    {
        private readonly bool _shouldFail;

        public FakeConnection(
            int connectionId, long rangeStart, long rangeEnd, long alreadyDownloaded,
            string chunkFilePath, RequestOptions? options, bool shouldFail)
        {
            ConnectionId = connectionId;
            RangeStart = rangeStart;
            RangeEnd = rangeEnd;
            AlreadyDownloaded = alreadyDownloaded;
            ChunkFilePath = chunkFilePath;
            Options = options;
            _shouldFail = shouldFail;
        }

        public int ConnectionId { get; }
        public long RangeStart { get; }
        public long RangeEnd { get; }
        public long AlreadyDownloaded { get; }
        public string ChunkFilePath { get; }
        public RequestOptions? Options { get; }

        public long BytesDownloaded { get; private set; }
        public int RetryCount => 0;
        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Idle;
        public string? LastError { get; private set; }

        public Task RunAsync(CancellationToken cancellationToken)
        {
            if (_shouldFail)
            {
                Status = ConnectionStatus.Failed;
                LastError = "the server hung up";
                throw new IOException("the server hung up");
            }

            BytesDownloaded = RangeEnd - RangeStart + 1;
            Status = ConnectionStatus.Finished;
            return Task.CompletedTask;
        }
    }
}
