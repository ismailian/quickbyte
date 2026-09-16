using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using QuickByte.Core.Enums;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.Core.Services;

namespace QuickByte.Core.Tests.Services;

/// <summary>
/// Handing a finished connection's slot to the back half of a slow one.
///
/// The property that matters above all is that the merged file is the file: no
/// byte fetched twice into two chunks, none left in neither. The end-to-end test
/// at the bottom proves that with real connections over a stubbed server that is
/// slow on purpose, and compares hashes; the rest pin the scheduling and the
/// layout bookkeeping with fakes.
/// </summary>
public sealed class DynamicSegmentationTests
{
    private const long MiB = 1024 * 1024;

    // ------------------------------------------------------------ splitting --

    [Fact]
    public async Task A_finished_connection_s_slot_goes_to_the_back_half_of_a_slow_one()
    {
        using var folder = new TempFolder();
        var factory = new ResizableFactory(blocks: chunk => chunk == 1);
        var pool = new ConnectionPoolManager(factory);
        var running = pool.RunAsync(Item(folder, 2), Info(8 * MiB), Settings(), default);

        await factory.WhenCreated(3);
        factory.ReleaseAll();
        Assert.True(await running);

        var slow = factory.Created.Single(c => c.ConnectionId == 1);
        Assert.True(slow.RangeEnd < 8 * MiB - 1, "the slow range was never shortened");
        AssertCoversEveryByteOnce(factory.Created, 8 * MiB);

        var map = SegmentMap.Read(folder.Path)!;
        Assert.True(map.Describes(8 * MiB));
        Assert.Equal(factory.Created.Count, map.Segments.Count);
    }

    [Fact]
    public async Task No_more_connections_run_at_once_than_the_download_allows()
    {
        using var folder = new TempFolder();
        var factory = new ResizableFactory(blocks: chunk => chunk % 2 == 1, finishDelay: TimeSpan.FromMilliseconds(5));
        var pool = new ConnectionPoolManager(factory);
        var running = pool.RunAsync(Item(folder, 3), Info(32 * MiB), Settings(), default);

        await factory.WhenCreated(6);
        factory.ReleaseAll();
        await running;

        Assert.True(factory.MaxConcurrent <= 3, $"{factory.MaxConcurrent} ran at once");
    }

    [Fact]
    public async Task A_range_too_small_to_repay_a_new_connection_is_left_alone()
    {
        using var folder = new TempFolder();
        var factory = new ResizableFactory(blocks: chunk => chunk == 1);
        var pool = new ConnectionPoolManager(factory);
        var running = pool.RunAsync(Item(folder, 2), Info(ConnectionPoolManager.MinimumSplitBytes), Settings(), default);

        await Task.Delay(50);
        factory.ReleaseAll();
        await running;

        Assert.Equal(2, factory.Created.Count);
    }

    [Fact]
    public async Task Turned_off_nothing_is_split()
    {
        using var folder = new TempFolder();
        var factory = new ResizableFactory(blocks: chunk => chunk == 1);
        var pool = new ConnectionPoolManager(factory);
        var running = pool.RunAsync(Item(folder, 2), Info(8 * MiB), Settings(dynamic: false), default);

        await Task.Delay(50);
        factory.ReleaseAll();
        await running;

        Assert.Equal(2, factory.Created.Count);
    }

    // --------------------------------------- a server that refuses new ones --

    [Fact]
    public async Task A_split_off_range_the_server_refuses_is_tried_again_and_the_download_still_completes()
    {
        // Measured against a real server: a split opens a new connection, and a
        // rate limiter answers 429. Splitting must never be why a download fails.
        using var folder = new TempFolder();
        var factory = new ResizableFactory(blocks: chunk => chunk == 1, fails: (chunk, attempt) => chunk >= 2 && attempt == 1);
        var pool = new ConnectionPoolManager(factory) { SplitRetryBaseDelay = TimeSpan.FromMilliseconds(10) };
        var failures = new List<string>();
        pool.ConnectionFailed += (_, message) => failures.Add(message);

        var running = pool.RunAsync(Item(folder, 2), Info(8 * MiB), Settings(), default);
        await factory.WhenCreated(4); // 0, 1, the split-off 2, and 2 again
        factory.ReleaseAll();

        Assert.True(await running);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task The_first_refusal_stops_any_more_splitting()
    {
        using var folder = new TempFolder();
        var factory = new ResizableFactory(blocks: chunk => chunk == 1, fails: (chunk, attempt) => chunk == 2 && attempt == 1);
        var pool = new ConnectionPoolManager(factory) { SplitRetryBaseDelay = TimeSpan.FromMilliseconds(10) };

        var running = pool.RunAsync(Item(folder, 2), Info(64 * MiB), Settings(), default);
        await factory.WhenCreated(4);
        await Task.Delay(100);
        factory.ReleaseAll();
        await running;

        // Chunk 2 twice, and never a chunk 3.
        Assert.Equal(new[] { 0, 1, 2, 2 }, factory.Created.Select(c => c.ConnectionId).Order());
    }

    [Fact]
    public async Task A_split_off_range_that_keeps_failing_is_reported_in_the_end()
    {
        using var folder = new TempFolder();
        var factory = new ResizableFactory(blocks: chunk => chunk == 1, fails: (chunk, _) => chunk == 2);
        var pool = new ConnectionPoolManager(factory) { SplitRetryBaseDelay = TimeSpan.FromMilliseconds(5) };
        var failures = new List<string>();
        pool.ConnectionFailed += (_, message) => failures.Add(message);

        var running = pool.RunAsync(Item(folder, 2), Info(8 * MiB), Settings(), default);
        await factory.WhenCreated(2 + ConnectionPoolManager.SplitAttempts);
        factory.ReleaseAll();

        Assert.False(await running);
        Assert.Single(failures);
        Assert.Equal(ConnectionPoolManager.SplitAttempts, factory.Created.Count(c => c.ConnectionId == 2));
    }

    [Fact]
    public async Task An_original_range_that_fails_is_reported_straight_away()
    {
        using var folder = new TempFolder();
        var factory = new ResizableFactory(blocks: _ => false, fails: (chunk, _) => chunk == 0);
        var pool = new ConnectionPoolManager(factory);
        var failures = new List<string>();
        pool.ConnectionFailed += (_, message) => failures.Add(message);

        Assert.False(await pool.RunAsync(Item(folder, 2), Info(8 * MiB), Settings(), default));

        Assert.Single(failures);
        Assert.Single(factory.Created, c => c.ConnectionId == 0);
    }

    // --------------------------------------------------------------- resume --

    [Fact]
    public async Task A_resume_follows_the_saved_layout_and_its_chunks()
    {
        using var folder = new TempFolder();
        SegmentMap.Write(folder.Path, new SegmentMap
        {
            TotalBytes = 1000,
            Segments = { new(0, 0, 499), new(1, 500, 749), new(2, 750, 999) }
        });
        folder.WriteFile("part1.tmp", new byte[100]);
        folder.WriteFile("part2.tmp", new byte[250]);

        var factory = new ResizableFactory(blocks: _ => false);
        await new ConnectionPoolManager(factory).RunAsync(Item(folder, 2), Info(1000), Settings(), default);

        var byChunk = factory.Created.ToDictionary(c => c.ConnectionId);
        Assert.Equal((500, 749, 100L), (byChunk[1].RangeStart, byChunk[1].RangeEnd, byChunk[1].AlreadyDownloaded));
        Assert.Equal((750, 999, 250L), (byChunk[2].RangeStart, byChunk[2].RangeEnd, byChunk[2].AlreadyDownloaded));
    }

    [Fact]
    public async Task A_resume_with_more_unfinished_segments_than_connections_runs_them_a_few_at_a_time()
    {
        using var folder = new TempFolder();
        SegmentMap.Write(folder.Path, new SegmentMap
        {
            TotalBytes = 500,
            Segments = { new(0, 0, 99), new(1, 100, 199), new(2, 200, 299), new(3, 300, 399), new(4, 400, 499) }
        });

        var factory = new ResizableFactory(blocks: _ => false, finishDelay: TimeSpan.FromMilliseconds(10));
        await new ConnectionPoolManager(factory).RunAsync(Item(folder, 2), Info(500), Settings(), default);

        Assert.Equal(5, factory.Created.Count);
        Assert.True(factory.MaxConcurrent <= 2);
    }

    [Fact]
    public async Task A_layout_for_a_file_of_another_length_is_replaced_and_its_chunks_discarded()
    {
        using var folder = new TempFolder();
        SegmentMap.Write(folder.Path, new SegmentMap { TotalBytes = 900, Segments = { new(0, 0, 449), new(7, 450, 899) } });
        folder.WriteFile("part7.tmp", new byte[10]);

        var factory = new ResizableFactory(blocks: _ => false);
        await new ConnectionPoolManager(factory).RunAsync(Item(folder, 2), Info(1000), Settings(), default);

        Assert.Equal(new[] { 0, 1 }, factory.Created.Select(c => c.ConnectionId).Order());
        Assert.False(File.Exists(folder.File("part7.tmp")));
        Assert.True(SegmentMap.Read(folder.Path)!.Describes(1000));
    }

    [Fact]
    public async Task A_chunk_the_layout_does_not_name_discards_the_set_rather_than_trusting_it()
    {
        using var folder = new TempFolder();
        SegmentMap.Write(folder.Path, new SegmentMap { TotalBytes = 1000, Segments = { new(0, 0, 499), new(1, 500, 999) } });
        folder.WriteFile("part0.tmp", new byte[100]);
        folder.WriteFile("part5.tmp", new byte[100]);

        var factory = new ResizableFactory(blocks: _ => false);
        await new ConnectionPoolManager(factory).RunAsync(Item(folder, 2), Info(1000), Settings(), default);

        Assert.All(factory.Created, c => Assert.Equal(0, c.AlreadyDownloaded));
    }

    [Fact]
    public async Task A_download_that_cannot_be_split_keeps_no_layout()
    {
        using var folder = new TempFolder();
        SegmentMap.Write(folder.Path, new SegmentMap { TotalBytes = 1000, Segments = { new(0, 0, 999) } });

        await new ConnectionPoolManager(new ResizableFactory(_ => false)).RunAsync(
            Item(folder, 8), new RemoteFileInfo { ContentLength = 1000, SupportsRangeRequests = false }, Settings(), default);

        Assert.False(File.Exists(folder.File(SegmentMap.FileName)));
    }

    [Fact]
    public void Discarding_the_chunks_discards_the_layout_and_forgetting_it_keeps_the_chunks()
    {
        using var folder = new TempFolder();
        var pool = new ConnectionPoolManager(new ResizableFactory(_ => false));

        SegmentMap.Write(folder.Path, new SegmentMap { TotalBytes = 1, Segments = { new(0, 0, 0) } });
        folder.WriteFile("part0.tmp", new byte[1]);
        pool.ForgetLayout(folder.Path);
        Assert.False(File.Exists(folder.File(SegmentMap.FileName)));
        Assert.True(File.Exists(folder.File("part0.tmp")));

        SegmentMap.Write(folder.Path, new SegmentMap { TotalBytes = 1, Segments = { new(0, 0, 0) } });
        pool.DiscardChunks(folder.Path);
        Assert.False(File.Exists(folder.File(SegmentMap.FileName)));
    }

    // ------------------------------------------------------------ end to end --

    [Fact]
    public async Task Real_connections_split_mid_transfer_still_merge_into_exactly_the_file()
    {
        // The first half is served at full speed and the second deliberately
        // slowly, so the fast connection finishes while the slow one still has
        // megabytes to go — and the pool splits it, possibly more than once,
        // with the old connection's response still streaming past the new end.
        using var folder = new TempFolder();
        byte[] content = RandomNumberGenerator.GetBytes((int)(6 * MiB));
        using var server = new RangeServer(content, slowFrom: 3 * MiB);
        var factory = new RealFactory(server.Client);
        var settings = Settings();
        settings.StreamBufferSizeBytes = 64 * 1024;

        var item = Item(folder, 2);
        var pool = new ConnectionPoolManager(factory);
        bool finished = await pool.RunAsync(item, Info(content.Length), settings, default);

        Assert.True(finished);
        Assert.True(factory.Created > 2, "the slow connection was never split");

        string destination = folder.File("merged.bin");
        await new FileMerger().MergeAsync(folder.Path, pool.GetOrderedChunkPaths(), destination, 64 * 1024, null, default);

        Assert.Equal(SHA256.HashData(content), SHA256.HashData(File.ReadAllBytes(destination)));
    }

    // ------------------------------------------------------------- plumbing --

    private static DownloadItem Item(TempFolder folder, int connections) => new()
    {
        Url = "https://example.com/file.bin",
        ConnectionsCount = connections,
        TempFolderPath = folder.Path
    };

    private static RemoteFileInfo Info(long length) => new() { ContentLength = length, SupportsRangeRequests = true };

    private static DownloadSettings Settings(bool dynamic = true) => new()
    {
        DynamicSegmentation = dynamic,
        MaxRetries = 0,
        RetryDelayMilliseconds = 0
    };

    private static void AssertCoversEveryByteOnce(IEnumerable<IDownloadConnection> connections, long total)
    {
        long next = 0;
        foreach (var c in connections.OrderBy(c => c.RangeStart))
        {
            Assert.Equal(next, c.RangeStart);
            next = c.RangeEnd + 1;
        }
        Assert.Equal(total, next);
    }

    /// <summary>Resizable fakes: a chunk that blocks runs until released, the rest finish at once.</summary>
    private sealed class ResizableFactory : IConnectionFactory
    {
        private readonly Func<int, bool> _blocks;
        private readonly Func<int, int, bool> _fails;
        private readonly Dictionary<int, int> _attempts = new();
        private readonly TimeSpan _finishDelay;
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object _gate = new();
        private int _running;

        public ResizableFactory(Func<int, bool> blocks, TimeSpan finishDelay = default, Func<int, int, bool>? fails = null)
        {
            _blocks = blocks;
            _finishDelay = finishDelay;
            _fails = fails ?? ((_, _) => false);
        }

        public List<FakeResizable> Created { get; } = new();
        public int MaxConcurrent { get; private set; }

        public void ReleaseAll() => _release.TrySetResult();

        public async Task WhenCreated(int count)
        {
            for (int i = 0; i < 400; i++)
            {
                lock (_gate) if (Created.Count >= count) return;
                await Task.Delay(5);
            }
            throw new TimeoutException($"only {Created.Count} connections were created");
        }

        public IDownloadConnection Create(
            int connectionId, string url, long rangeStart, long rangeEnd, long alreadyDownloaded,
            string chunkFilePath, DownloadSettings settings, IBandwidthLimiter? bandwidthLimiter = null, RequestOptions? options = null)
        {
            FakeResizable connection;
            lock (_gate)
            {
                int attempt = _attempts[connectionId] = _attempts.GetValueOrDefault(connectionId) + 1;
                connection = new FakeResizable(this, connectionId, rangeStart, rangeEnd, alreadyDownloaded, chunkFilePath, _blocks(connectionId))
                {
                    Fails = _fails(connectionId, attempt)
                };
                Created.Add(connection);
            }
            return connection;
        }

        public async Task RunAsync(FakeResizable connection, CancellationToken token)
        {
            lock (_gate) MaxConcurrent = Math.Max(MaxConcurrent, ++_running);
            try
            {
                if (connection.Blocks) await _release.Task.WaitAsync(token);
                else if (_finishDelay > TimeSpan.Zero) await Task.Delay(_finishDelay, token);
            }
            finally
            {
                lock (_gate) _running--;
            }
        }
    }

    private sealed class FakeResizable : IResizableConnection
    {
        private readonly ResizableFactory _factory;
        private readonly SegmentRange _range;

        public FakeResizable(ResizableFactory factory, int id, long start, long end, long already, string path, bool blocks)
        {
            _factory = factory;
            _range = new SegmentRange(start, end);
            ConnectionId = id;
            AlreadyDownloaded = already;
            ChunkFilePath = path;
            Blocks = blocks;
        }

        public bool Blocks { get; }
        public bool Fails { get; init; }
        public long AlreadyDownloaded { get; }
        public int ConnectionId { get; }
        public long RangeStart => _range.Start;
        public long RangeEnd => _range.End;
        public long BytesDownloaded { get; private set; }
        public int RetryCount => 0;
        public ConnectionStatus Status { get; private set; } = ConnectionStatus.Idle;
        public string? LastError => null;
        public string ChunkFilePath { get; }

        public long CommittedEnd => _range.CommittedEnd(RangeStart + BytesDownloaded);
        public bool TryShrinkEnd(long newEnd) => _range.TryShrinkEnd(newEnd, RangeStart + BytesDownloaded);

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            Status = ConnectionStatus.ReceivingData;
            if (Fails)
            {
                Status = ConnectionStatus.Failed;
                throw new HttpRequestException("Response status code does not indicate success: 429 (Too Many Requests).");
            }
            await _factory.RunAsync(this, cancellationToken);
            BytesDownloaded = RangeEnd - RangeStart + 1;
            Status = ConnectionStatus.Finished;
        }
    }

    private sealed class RealFactory : IConnectionFactory
    {
        private readonly HttpClient _client;
        private int _created;

        public RealFactory(HttpClient client) => _client = client;

        public int Created => _created;

        public IDownloadConnection Create(
            int connectionId, string url, long rangeStart, long rangeEnd, long alreadyDownloaded,
            string chunkFilePath, DownloadSettings settings, IBandwidthLimiter? bandwidthLimiter = null, RequestOptions? options = null)
        {
            Interlocked.Increment(ref _created);
            return new DownloadConnection(_client, connectionId, url, rangeStart, rangeEnd, alreadyDownloaded, chunkFilePath, settings, bandwidthLimiter, options);
        }
    }

    /// <summary>
    /// Serves ranges of a byte array, slowly from <c>slowFrom</c> onwards. Always
    /// streams to the end of the requested range, the way a real server does — so a
    /// connection shortened mid-transfer is still being sent bytes past its new end.
    /// </summary>
    private sealed class RangeServer : IDisposable
    {
        public RangeServer(byte[] content, long slowFrom) =>
            Client = new HttpClient(new Handler(content, slowFrom));

        public HttpClient Client { get; }

        public void Dispose() => Client.Dispose();

        private sealed class Handler : HttpMessageHandler
        {
            private readonly byte[] _content;
            private readonly long _slowFrom;

            public Handler(byte[] content, long slowFrom)
            {
                _content = content;
                _slowFrom = slowFrom;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var range = request.Headers.Range!.Ranges.Single();
                long from = range.From ?? 0;
                long to = range.To ?? _content.Length - 1;

                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new StreamContent(new PacedStream(_content, from, to, slow: from >= _slowFrom)),
                    RequestMessage = request
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, _content.Length);
                return Task.FromResult(response);
            }
        }

        private sealed class PacedStream : Stream
        {
            private readonly byte[] _content;
            private readonly long _end;
            private readonly bool _slow;
            private long _position;

            public PacedStream(byte[] content, long from, long to, bool slow)
            {
                _content = content;
                _position = from;
                _end = to;
                _slow = slow;
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_position > _end) return 0;
                if (_slow) await Task.Delay(8, cancellationToken);

                int count = (int)Math.Min(Math.Min(buffer.Length, 64 * 1024), _end - _position + 1);
                _content.AsMemory((int)_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            public override int Read(byte[] buffer, int offset, int count) =>
                ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
