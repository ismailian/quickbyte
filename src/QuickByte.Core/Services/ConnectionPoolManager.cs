using System.Threading;
using QuickByte.Core.Enums;
using QuickByte.Core.Events;
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
    private readonly IConnectionFactory _connectionFactory;
    private readonly IBandwidthLimiter? _bandwidthLimiter;
    private readonly SpeedCalculator _speedCalculator = new();
    private List<IDownloadConnection> _connections = new();
    private Timer? _reportTimer;
    private Guid _downloadId;
    private long _totalBytes;
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

        try
        {
            var tasks = _connections.Select(c => RunConnectionSafeAsync(c, cancellationToken)).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            await _reportTimer.DisposeAsync().ConfigureAwait(false);
            _reportTimer = null;
            ReportProgress(); // final, exact snapshot
        }

        cancellationToken.ThrowIfCancellationRequested();

        bool allFinished = _connections.All(c => c.Status == ConnectionStatus.Finished);
        return allFinished;
    }

    private List<IDownloadConnection> BuildConnections(DownloadItem item, RemoteFileInfo fileInfo, int connectionsCount, DownloadSettings settings)
    {
        var connections = new List<IDownloadConnection>(connectionsCount);

        // Resolved once and shared: every connection of a download must present
        // the same login and the same captured headers, or the segments come
        // back from different things.
        var options = item.ToRequestOptions();

        if (connectionsCount == 1 || !fileInfo.HasKnownSize)
        {
            string chunkPath = Path.Combine(item.TempFolderPath, "part0.tmp");
            long already = File.Exists(chunkPath) ? new FileInfo(chunkPath).Length : 0;
            long end = fileInfo.HasKnownSize ? fileInfo.ContentLength - 1 : long.MaxValue - 1;
            connections.Add(_connectionFactory.Create(0, item.Url, 0, end, already, chunkPath, settings, _bandwidthLimiter, options));
            return connections;
        }

        var ranges = RangeSplitter.Split(fileInfo.ContentLength, connectionsCount);
        for (int i = 0; i < ranges.Count; i++)
        {
            string chunkPath = Path.Combine(item.TempFolderPath, $"part{i}.tmp");
            long already = File.Exists(chunkPath) ? new FileInfo(chunkPath).Length : 0;
            connections.Add(_connectionFactory.Create(i, item.Url, ranges[i].Start, ranges[i].End, already, chunkPath, settings, _bandwidthLimiter, options));
        }
        return connections;
    }

    private async Task RunConnectionSafeAsync(IDownloadConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on pause/stop — leave partial chunk in place for resume.
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
