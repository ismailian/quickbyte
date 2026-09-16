using System.Globalization;
using System.Threading;
using QuickByte.Core.Enums;
using QuickByte.Core.Events;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// Oversees the full set of connections for one download: splits the byte
/// range (via <see cref="RangeSplitter"/>), assigns each connection its
/// range and temp chunk path (via <see cref="IConnectionFactory"/>), runs
/// them concurrently, and raises throttled, aggregated progress/connection
/// snapshot events on a timer so the UI stays smooth regardless of how many
/// connections are running. Once every connection finishes, control returns
/// to the caller (<see cref="DownloadService"/>) which triggers the merge.
/// </summary>
/// <remarks>
/// <para><b>Dynamic segmentation.</b> A fixed split is only as fast as its slowest
/// segment: when seven connections finish and the eighth crawls, the download
/// crawls with it on one connection. So when a connection finishes, its slot is
/// given to the back half of whichever running range has the most left — that
/// range is shortened (safely, see <see cref="SegmentRange"/>) and a new
/// connection fetches the rest into a chunk of its own. The number running never
/// exceeds the download's connection count, which is all a server that limits
/// connections was ever promised. Ranges under <see cref="MinimumSplitBytes"/> are
/// left alone: a new connection costs a round trip that a small tail does not
/// repay.</para>
///
/// <para><b>The layout is written down.</b> Once ranges can be split, a chunk's
/// number no longer says which bytes it holds, so <see cref="SegmentMap"/> records
/// it in the temp folder — before the new connection writes a byte. Resume reads
/// the map; a download without one is the fixed split and gets one.</para>
/// </remarks>
public sealed class ConnectionPoolManager : IConnectionPoolManager
{
    /// <summary>Chunk files are <c>part0.tmp</c>, <c>part1.tmp</c>… — named here because resume also has to recognise them.</summary>
    private const string ChunkPrefix = "part";

    /// <summary>A range must have at least this much left to be worth splitting: each half is then 512 KiB or more.</summary>
    internal const long MinimumSplitBytes = 1024 * 1024;

    /// <summary>How many times a range created by a split is tried before its failure counts.</summary>
    internal const int SplitAttempts = 3;

    /// <summary>
    /// The wait before a split-off range is tried again, doubled each time. Long
    /// enough for a rate limiter's window to move on. Settable for the tests.
    /// </summary>
    internal TimeSpan SplitRetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    private readonly IConnectionFactory _connectionFactory;
    private readonly IBandwidthLimiter? _bandwidthLimiter;
    private readonly SpeedCalculator _speedCalculator = new();
    private List<IDownloadConnection> _connections = new();
    private Timer? _reportTimer;
    private Guid _downloadId;
    private long _totalBytes;
    private RemoteFileChangedException? _fileChanged;
    private readonly object _sync = new();

    // Per run: what a split needs to build a connection like the others.
    private SegmentMap? _map;
    private DownloadItem? _item;
    private DownloadSettings? _settings;
    private RequestOptions? _options;

    public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    public event EventHandler<ConnectionsSnapshotEventArgs>? ConnectionsChanged;
    public event EventHandler<string>? ConnectionFailed;

    /// <param name="bandwidthLimiter">
    /// Handed to every connection this pool builds, so one budget covers the
    /// whole download instead of each segment getting its own. Unlike the rest
    /// of <see cref="DownloadSettings"/>, which is snapshotted when the pool
    /// starts, the limiter is a live object — changing its rate takes effect on
    /// the transfer already running.
    /// </param>
    public ConnectionPoolManager(IConnectionFactory connectionFactory, IBandwidthLimiter? bandwidthLimiter = null)
    {
        _connectionFactory = connectionFactory;
        _bandwidthLimiter = bandwidthLimiter;
    }

    public IReadOnlyList<ConnectionInfo> Snapshot => BuildSnapshot();

    public async Task<bool> RunAsync(DownloadItem item, RemoteFileInfo fileInfo, DownloadSettings settings, CancellationToken cancellationToken)
    {
        _downloadId = item.Id;
        _totalBytes = fileInfo.ContentLength;
        _item = item;
        _settings = settings;

        // Resolved once and shared: every connection of a download must present
        // the same login and the same captured headers, or the segments come
        // back from different things.
        _options = item.ToRequestOptions();

        bool segmentable = fileInfo.SupportsRangeRequests && fileInfo.HasKnownSize;
        int concurrency = segmentable ? settings.ClampConnections(item.ConnectionsCount) : 1;

        Directory.CreateDirectory(item.TempFolderPath);

        var built = BuildConnections(item, fileInfo, concurrency, settings);
        lock (_sync)
        {
            _connections = built;
        }

        // Floor the sampling interval: the windows interpolate between samples,
        // and anything slower than ~50 ms starts to feel like stepping.
        int interval = Math.Clamp(settings.ProgressUpdateIntervalMilliseconds, 50, 2000);
        _reportTimer = new Timer(_ => ReportProgress(), null, 0, interval);

        // Linked rather than the caller's token directly: one connection learning
        // that the file changed on the server has to stop all of them, and that
        // is not a pause the caller asked for.
        _fileChanged = null;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            bool splitting = segmentable && settings.DynamicSegmentation && _map is not null;
            await RunScheduledAsync(built, concurrency, splitting, linked).ConfigureAwait(false);
        }
        finally
        {
            await _reportTimer.DisposeAsync().ConfigureAwait(false);
            _reportTimer = null;
            ReportProgress(); // final, exact snapshot
        }

        cancellationToken.ThrowIfCancellationRequested();

        // Every chunk belongs to the version that changed, not just the one whose
        // connection noticed, so this is the download's decision rather than a
        // failed connection's. DownloadService makes it.
        if (_fileChanged is not null) throw _fileChanged;

        List<IDownloadConnection> all;
        lock (_sync) all = _connections;
        return all.All(c => c.Status == ConnectionStatus.Finished);
    }

    /// <summary>
    /// Runs the connections at most <paramref name="concurrency"/> at a time — a
    /// resumed download may have more unfinished segments than that after earlier
    /// splits — and, when <paramref name="splitting"/>, gives each freed slot to
    /// the back half of the running range with the most left.
    /// </summary>
    /// <remarks>
    /// Splitting is an optimisation, and must never be the reason a download
    /// fails. Every split opens a new connection and abandons the old one's socket
    /// mid-response, and a server that rate-limits connections answers that churn
    /// with <c>429 Too Many Requests</c> — measured, not supposed. So the first
    /// failure of any kind stops splitting for the rest of the run, and a range
    /// that only exists because of a split is not allowed to fail the download on
    /// its first refusal: it waits out a backoff and is tried again, and only a
    /// range that still cannot be fetched after <see cref="SplitAttempts"/> tries is
    /// reported.
    /// </remarks>
    private async Task RunScheduledAsync(
        IReadOnlyList<IDownloadConnection> connections, int concurrency, bool splitting, CancellationTokenSource run)
    {
        var pending = new Queue<Scheduled>(connections.Select(c => new Scheduled(c, Attempt: 1, SplitOff: false)));
        var running = new Dictionary<Task<Outcome>, Scheduled>();

        void Start(Scheduled entry) =>
            running[RunConnectionSafeAsync(entry.Connection, run, RetryDelay(entry))] = entry;

        void Fill()
        {
            while (running.Count < concurrency && pending.Count > 0 && !run.IsCancellationRequested)
                Start(pending.Dequeue());

            while (splitting && running.Count < concurrency && !run.IsCancellationRequested
                   && TrySplit(running.Values.Select(entry => entry.Connection)) is { } created)
            {
                Start(new Scheduled(created, Attempt: 1, SplitOff: true));
            }
        }

        Fill();
        while (running.Count > 0)
        {
            var done = await Task.WhenAny(running.Keys).ConfigureAwait(false);
            var entry = running[done];
            running.Remove(done);

            if (done.Result is Outcome.Failed failure)
            {
                // The server has had enough of new connections, or the network has;
                // either way, no more of them this run.
                splitting = false;

                if (entry.SplitOff && entry.Attempt < SplitAttempts && !run.IsCancellationRequested)
                    pending.Enqueue(entry with { Connection = Recreate(entry.Connection), Attempt = entry.Attempt + 1 });
                else
                    ConnectionFailed?.Invoke(this, $"Connection #{entry.Connection.ConnectionId}: {failure.Message}");
            }

            Fill();
        }
    }

    private TimeSpan RetryDelay(Scheduled entry) =>
        entry.Attempt <= 1 ? TimeSpan.Zero : SplitRetryBaseDelay * Math.Pow(2, entry.Attempt - 2);

    /// <summary>
    /// A fresh connection for a range whose connection failed, carrying on from
    /// what that one left on disk, in place of it in the connection list — the list
    /// is what "did every connection finish" is asked of.
    /// </summary>
    private IDownloadConnection Recreate(IDownloadConnection failed)
    {
        long already = File.Exists(failed.ChunkFilePath) ? new FileInfo(failed.ChunkFilePath).Length : 0;
        var replacement = _connectionFactory.Create(
            failed.ConnectionId, _item!.Url, failed.RangeStart, failed.RangeEnd, already,
            failed.ChunkFilePath, _settings!, _bandwidthLimiter, _options);

        lock (_sync)
        {
            _connections = _connections.Select(c => ReferenceEquals(c, failed) ? replacement : c).ToList();
        }
        return replacement;
    }

    private sealed record Scheduled(IDownloadConnection Connection, int Attempt, bool SplitOff);

    private abstract record Outcome
    {
        public sealed record Done : Outcome;
        public sealed record Failed(string Message) : Outcome;
    }

    /// <summary>
    /// Halves the running range with the most left and returns a connection for
    /// its back half, or null when nothing is worth splitting. The map is written
    /// before the new connection exists, so a crash at any point leaves a layout
    /// that describes every byte on disk.
    /// </summary>
    private IDownloadConnection? TrySplit(IEnumerable<IDownloadConnection> running)
    {
        if (_map is null || _item is null || _settings is null) return null;
        if (_map.Segments.Count >= SegmentMap.MaxSegments) return null;

        var candidates = running
            .OfType<IResizableConnection>()
            .Where(c => c.Status is not (ConnectionStatus.Finished or ConnectionStatus.Failed))
            .Select(c => (Connection: c, From: c.CommittedEnd + 1, End: c.RangeEnd))
            .Where(c => c.End != RangeSplitter.UnboundedEnd)
            .OrderByDescending(c => c.End - c.From + 1)
            .ToList();

        foreach (var (connection, from, end) in candidates)
        {
            long remaining = end - from + 1;
            if (remaining < MinimumSplitBytes) return null; // sorted: nothing after is bigger

            long newEnd = from + remaining / 2 - 1;

            // Refused when the connection read past the split point in the moment
            // since it was measured; the next largest may still do.
            if (!connection.TryShrinkEnd(newEnd)) continue;

            var segment = new SegmentMap.Segment(_map.NextChunk, newEnd + 1, end);
            _map = _map.WithSplit(connection.ConnectionId, newEnd, segment);
            SegmentMap.Write(_item.TempFolderPath, _map);

            string chunkPath = ChunkPath(_item.TempFolderPath, segment.Chunk);
            try { File.Delete(chunkPath); } catch { /* best-effort: the chunk number is new */ }

            var created = _connectionFactory.Create(
                segment.Chunk, _item.Url, segment.Start, segment.End, 0, chunkPath, _settings, _bandwidthLimiter, _options);

            lock (_sync) _connections = new List<IDownloadConnection>(_connections) { created };
            return created;
        }

        return null;
    }

    private List<IDownloadConnection> BuildConnections(DownloadItem item, RemoteFileInfo fileInfo, int connectionsCount, DownloadSettings settings)
    {
        IReadOnlyList<(int Chunk, RangeSplitter.Range Range)> layout;

        if (fileInfo.SupportsRangeRequests && fileInfo.HasKnownSize)
        {
            _map = LoadOrCreateMap(item.TempFolderPath, fileInfo.ContentLength, connectionsCount);
            layout = _map.Segments
                .OrderBy(s => s.Start)
                .Select(s => (s.Chunk, new RangeSplitter.Range(s.Start, s.End)))
                .ToList();
        }
        else
        {
            // One connection: the whole file, or an open-ended range when the
            // server never said how big it is. There is no layout to keep, and a
            // map left by a run that could split would describe chunks that are
            // about to be discarded.
            _map = null;
            SegmentMap.Delete(item.TempFolderPath);

            var single = new RangeSplitter.Range(
                0, fileInfo.HasKnownSize ? fileInfo.ContentLength - 1 : RangeSplitter.UnboundedEnd);
            DiscardChunksFromADifferentSplit(item.TempFolderPath, new[] { (0, single) });
            layout = new[] { (0, single) };
        }

        var connections = new List<IDownloadConnection>(layout.Count);
        foreach (var (chunk, range) in layout)
        {
            string chunkPath = ChunkPath(item.TempFolderPath, chunk);
            long already = File.Exists(chunkPath) ? new FileInfo(chunkPath).Length : 0;
            connections.Add(_connectionFactory.Create(
                chunk, item.Url, range.Start, range.End, already, chunkPath, settings, _bandwidthLimiter, _options));
        }
        return connections;
    }

    /// <summary>
    /// The layout to resume from: the saved map when it describes this file and
    /// the chunks on disk agree with it, else a fresh fixed split — which, for a
    /// download from before maps existed, is exactly the layout its chunks have.
    /// </summary>
    private static SegmentMap LoadOrCreateMap(string tempFolder, long totalBytes, int connectionsCount)
    {
        var saved = SegmentMap.Read(tempFolder);
        if (saved is not null && saved.Describes(totalBytes))
        {
            var savedRanges = saved.Segments.Select(s => (s.Chunk, new RangeSplitter.Range(s.Start, s.End))).ToList();
            if (DiscardChunksFromADifferentSplit(tempFolder, savedRanges)) return saved;
        }

        var ranges = RangeSplitter.Split(totalBytes, connectionsCount);
        var layout = ranges.Select((range, i) => (i, range)).ToList();
        DiscardChunksFromADifferentSplit(tempFolder, layout);

        var map = SegmentMap.FromRanges(totalBytes, ranges);
        SegmentMap.Write(tempFolder, map);
        return map;
    }

    /// <summary>
    /// Throws away chunk files left by a run that split the file differently.
    /// Returns whether the chunks on disk fit <paramref name="layout"/> — nothing
    /// was discarded.
    ///
    /// Resume reads a chunk's length off disk and trusts it to be the head of
    /// that connection's range — which holds only while the layout stays the
    /// same, and it does not have to. Retry re-resolves the file, and a server
    /// that stops advertising byte ranges (or starts, or changes the length)
    /// moves the download between one connection and N. The old part0.tmp then
    /// holds a prefix of the whole file where the new split wants just the first
    /// eighth of it, every other chunk is orphaned or misread, and the merge
    /// writes the difference into the finished file without a word.
    ///
    /// Two things give a foreign chunk set away, and neither mistakes a run that
    /// was paused before every connection had opened its file for an
    /// incompatible one: a chunk the layout does not name, and a chunk longer than
    /// the range it would now be resuming.
    /// </summary>
    private static bool DiscardChunksFromADifferentSplit(
        string tempFolder, IReadOnlyList<(int Chunk, RangeSplitter.Range Range)> layout)
    {
        string[] existing;
        try { existing = Directory.GetFiles(tempFolder, $"{ChunkPrefix}*.tmp"); }
        catch { return true; /* best-effort: nothing to resume from is a safe answer */ }

        if (existing.Length == 0) return true;

        var ranges = layout.ToDictionary(entry => entry.Chunk, entry => entry.Range);
        bool compatible = true;
        foreach (string path in existing)
        {
            int index = ChunkIndexOf(path);
            if (index < 0) continue; // not a chunk name at all; not ours to judge

            if (!ranges.TryGetValue(index, out var range))
            {
                compatible = false;
                break;
            }

            long length;
            try { length = new FileInfo(path).Length; }
            catch { continue; }

            if (length > range.End - range.Start + 1)
            {
                compatible = false;
                break;
            }
        }

        if (compatible) return true;

        foreach (string path in existing)
        {
            if (ChunkIndexOf(path) < 0) continue;
            try { File.Delete(path); } catch { /* best-effort */ }
        }
        return false;
    }

    private static string ChunkPath(string tempFolder, int chunk) => Path.Combine(tempFolder, $"{ChunkPrefix}{chunk}.tmp");

    /// <returns>The <c>N</c> of a <c>partN.tmp</c>, or -1 for anything else.</returns>
    private static int ChunkIndexOf(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith(ChunkPrefix, StringComparison.Ordinal)
               && int.TryParse(name.AsSpan(ChunkPrefix.Length), NumberStyles.None,
                   CultureInfo.InvariantCulture, out int index)
            ? index
            : -1;
    }

    /// <summary>
    /// Runs one connection to its end and says how it ended, never throwing: the
    /// scheduler decides what a failure means, which depends on whether the range
    /// was one of the originals or split off from one.
    /// </summary>
    private async Task<Outcome> RunConnectionSafeAsync(IDownloadConnection connection, CancellationTokenSource run, TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, run.Token).ConfigureAwait(false);
            await connection.RunAsync(run.Token).ConfigureAwait(false);
            return new Outcome.Done();
        }
        catch (OperationCanceledException)
        {
            // Expected on pause/stop — leave partial chunk in place for resume.
            return new Outcome.Done();
        }
        catch (RemoteFileChangedException ex)
        {
            Interlocked.CompareExchange(ref _fileChanged, ex, null);

            // The siblings are appending bytes of a version that no longer exists
            // to chunks that are about to be judged. Stopping them now saves the
            // bandwidth; the chunks themselves are left for DownloadService.
            try { run.Cancel(); } catch (ObjectDisposedException) { /* the run already ended */ }
            return new Outcome.Done();
        }
        catch (Exception ex)
        {
            return new Outcome.Failed(ex.Message);
        }
    }

    private void ReportProgress()
    {
        List<IDownloadConnection> connectionsCopy;
        lock (_sync) { connectionsCopy = _connections; }
        if (connectionsCopy.Count == 0) return;

        long downloaded = connectionsCopy.Sum(c => c.BytesDownloaded);
        _speedCalculator.AddSample(downloaded);
        double speed = _speedCalculator.GetSpeedBytesPerSecond();
        var eta = SpeedCalculator.EstimateTimeRemaining(downloaded, _totalBytes, speed);

        ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
        {
            DownloadId = _downloadId,
            DownloadedBytes = downloaded,
            TotalBytes = _totalBytes,
            SpeedBytesPerSecond = speed,
            EstimatedTimeRemaining = eta
        });

        ConnectionsChanged?.Invoke(this, new ConnectionsSnapshotEventArgs
        {
            DownloadId = _downloadId,
            Connections = BuildSnapshot()
        });
    }

    private IReadOnlyList<ConnectionInfo> BuildSnapshot()
    {
        List<IDownloadConnection> connectionsCopy;
        lock (_sync) { connectionsCopy = _connections; }

        return connectionsCopy.Select(c => new ConnectionInfo
        {
            ConnectionId = c.ConnectionId,
            RangeStart = c.RangeStart,
            RangeEnd = c.RangeEnd,
            BytesDownloaded = c.BytesDownloaded,
            Status = c.Status,
            RetryCount = c.RetryCount,
            LastError = c.LastError
        }).ToList();
    }

    public void DiscardChunks(string tempFolder)
    {
        if (string.IsNullOrWhiteSpace(tempFolder)) return;

        // The layout goes with the chunks it described.
        SegmentMap.Delete(tempFolder);

        string[] chunks;
        try { chunks = Directory.GetFiles(tempFolder, $"{ChunkPrefix}*.tmp"); }
        catch { return; /* best-effort: a folder that is not there holds nothing to discard */ }

        foreach (string path in chunks)
        {
            // Only this pool's own names. The folder is the download's, but a
            // file in it that does not parse as a chunk was not put there by us.
            if (ChunkIndexOf(path) < 0) continue;
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

    public void ForgetLayout(string tempFolder)
    {
        if (!string.IsNullOrWhiteSpace(tempFolder)) SegmentMap.Delete(tempFolder);
    }

    public IReadOnlyList<string> GetOrderedChunkPaths()
    {
        lock (_sync)
        {
            return _connections
                .OrderBy(c => c.RangeStart)
                .Select(c => c.ChunkFilePath)
                .ToList();
        }
    }
}
