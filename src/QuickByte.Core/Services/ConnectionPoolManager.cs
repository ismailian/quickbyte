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
public sealed class ConnectionPoolManager : IConnectionPoolManager
{
    /// <summary>Chunk files are <c>part0.tmp</c>, <c>part1.tmp</c>… — named here because resume also has to recognise them.</summary>
    private const string ChunkPrefix = "part";

    private readonly IConnectionFactory _connectionFactory;
    private readonly IBandwidthLimiter? _bandwidthLimiter;
    private readonly SpeedCalculator _speedCalculator = new();
    private List<IDownloadConnection> _connections = new();
    private Timer? _reportTimer;
    private Guid _downloadId;
    private long _totalBytes;
    private RemoteFileChangedException? _fileChanged;
    private readonly object _sync = new();

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

        int connectionsCount = fileInfo.SupportsRangeRequests && fileInfo.HasKnownSize
            ? settings.ClampConnections(item.ConnectionsCount)
            : 1;

        Directory.CreateDirectory(item.TempFolderPath);

        lock (_sync)
        {
            _connections = BuildConnections(item, fileInfo, connectionsCount, settings);
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
            var tasks = _connections.Select(c => RunConnectionSafeAsync(c, linked)).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
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

        bool allFinished = _connections.All(c => c.Status == ConnectionStatus.Finished);
        return allFinished;
    }

    private List<IDownloadConnection> BuildConnections(DownloadItem item, RemoteFileInfo fileInfo, int connectionsCount, DownloadSettings settings)
    {
        // Resolved once and shared: every connection of a download must present
        // the same login and the same captured headers, or the segments come
        // back from different things.
        var options = item.ToRequestOptions();

        // The one-connection case is the same shape with a single range: either
        // the whole file, or an open-ended one when the server never said how big
        // it is. Expressing it that way is what lets the split check below apply
        // to both paths.
        IReadOnlyList<RangeSplitter.Range> ranges =
            connectionsCount == 1 || !fileInfo.HasKnownSize
                ? new[]
                {
                    new RangeSplitter.Range(
                        0, fileInfo.HasKnownSize ? fileInfo.ContentLength - 1 : RangeSplitter.UnboundedEnd)
                }
                : RangeSplitter.Split(fileInfo.ContentLength, connectionsCount);

        DiscardChunksFromADifferentSplit(item.TempFolderPath, ranges);

        var connections = new List<IDownloadConnection>(ranges.Count);
        for (int i = 0; i < ranges.Count; i++)
        {
            string chunkPath = Path.Combine(item.TempFolderPath, $"{ChunkPrefix}{i}.tmp");
            long already = File.Exists(chunkPath) ? new FileInfo(chunkPath).Length : 0;
            connections.Add(_connectionFactory.Create(
                i, item.Url, ranges[i].Start, ranges[i].End, already, chunkPath, settings, _bandwidthLimiter, options));
        }
        return connections;
    }

    /// <summary>
    /// Throws away chunk files left by a run that split the file differently.
    ///
    /// Resume reads a chunk's length off disk and trusts it to be the head of
    /// that connection's range — which holds only while the split stays the
    /// same, and it does not have to. Retry re-resolves the file, and a server
    /// that stops advertising byte ranges (or starts, or changes the length)
    /// moves the download between one connection and N. The old part0.tmp then
    /// holds a prefix of the whole file where the new split wants just the first
    /// eighth of it, every other chunk is orphaned or misread, and the merge
    /// writes the difference into the finished file without a word.
    ///
    /// Two things give a foreign chunk set away, and neither mistakes a run that
    /// was paused before every connection had opened its file for an
    /// incompatible one: a chunk numbered past the end of the new split, and a
    /// chunk longer than the range it would now be resuming.
    /// </summary>
    private static void DiscardChunksFromADifferentSplit(string tempFolder, IReadOnlyList<RangeSplitter.Range> ranges)
    {
        string[] existing;
        try { existing = Directory.GetFiles(tempFolder, $"{ChunkPrefix}*.tmp"); }
        catch { return; /* best-effort: nothing to resume from is a safe answer */ }

        if (existing.Length == 0) return;

        bool compatible = true;
        foreach (string path in existing)
        {
            int index = ChunkIndexOf(path);
            if (index < 0 || index >= ranges.Count)
            {
                compatible = false;
                break;
            }

            long length;
            try { length = new FileInfo(path).Length; }
            catch { continue; }

            if (length > ranges[index].End - ranges[index].Start + 1)
            {
                compatible = false;
                break;
            }
        }

        if (compatible) return;

        foreach (string path in existing)
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }
    }

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

    private async Task RunConnectionSafeAsync(IDownloadConnection connection, CancellationTokenSource run)
    {
        try
        {
            await connection.RunAsync(run.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on pause/stop — leave partial chunk in place for resume.
        }
        catch (RemoteFileChangedException ex)
        {
            Interlocked.CompareExchange(ref _fileChanged, ex, null);

            // The siblings are appending bytes of a version that no longer exists
            // to chunks that are about to be judged. Stopping them now saves the
            // bandwidth; the chunks themselves are left for DownloadService.
            try { run.Cancel(); } catch (ObjectDisposedException) { /* the run already ended */ }
        }
        catch (Exception ex)
        {
            ConnectionFailed?.Invoke(this, $"Connection #{connection.ConnectionId}: {ex.Message}");
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
