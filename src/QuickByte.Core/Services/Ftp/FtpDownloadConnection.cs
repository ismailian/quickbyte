using System.Threading;
using QuickByte.Core.Enums;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services.Ftp;

/// <summary>
/// The FTP counterpart of <see cref="DownloadConnection"/>: fetches exactly one
/// byte range into one chunk file, reports its own state, and knows nothing
/// about its siblings.
///
/// The differences from the HTTP version are both consequences of FTP having no
/// Range header:
/// <list type="bullet">
/// <item>Only the <em>start</em> of a segment can be requested (<c>REST</c>), so
/// the end is enforced here by counting bytes and closing the data connection —
/// the server would otherwise keep sending to the end of the file.</item>
/// <item>Every attempt opens its own control connection. There is no connection
/// pool to share: an FTP control channel is stateful (working directory,
/// transfer type, restart offset), so two segments cannot take turns on one.</item>
/// </list>
/// </summary>
public sealed class FtpDownloadConnection : IDownloadConnection
{
    private readonly Uri _uri;
    private readonly string _path;
    private readonly DownloadSettings _settings;
    private readonly IBandwidthLimiter _bandwidthLimiter;
    private readonly DownloadCredentials? _credentials;

    private long _bytesDownloaded;

    // Volatile for the same reason _status is: it is written on the thread
    // running the transfer and read by the pool's report timer.
    private volatile int _retryCount;
    private volatile ConnectionStatus _status = ConnectionStatus.Idle;
    private volatile string? _lastError;

    public int ConnectionId { get; }
    public long RangeStart { get; }
    public long RangeEnd { get; }
    public string ChunkFilePath { get; }

    public long BytesDownloaded => Interlocked.Read(ref _bytesDownloaded);
    public int RetryCount => _retryCount;
    public ConnectionStatus Status => _status;
    public string? LastError => _lastError;

    public FtpDownloadConnection(
        int connectionId,
        string url,
        long rangeStart,
        long rangeEnd,
        long alreadyDownloaded,
        string chunkFilePath,
        DownloadSettings settings,
        IBandwidthLimiter? bandwidthLimiter = null,
        RequestOptions? options = null)
    {
        ConnectionId = connectionId;
        _uri = new Uri(url);
        _path = FtpUrl.PathOf(_uri);
        RangeStart = rangeStart;
        RangeEnd = rangeEnd;
        ChunkFilePath = chunkFilePath;
        _settings = settings;
        _bandwidthLimiter = bandwidthLimiter ?? UnlimitedBandwidthLimiter.Instance;
        _credentials = options?.Credentials;
        _bytesDownloaded = Math.Clamp(alreadyDownloaded, 0, TotalBytes);
    }

    private long TotalBytes => RangeEnd - RangeStart + 1;

    /// <summary>
    /// True when this connection was handed no real end — see
    /// <see cref="RangeSplitter.UnboundedEnd"/>. The data connection closing is
    /// then the end of the file rather than a transfer cut short.
    /// </summary>
    private bool IsUnbounded => RangeEnd == RangeSplitter.UnboundedEnd;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (BytesDownloaded >= TotalBytes)
        {
            _status = ConnectionStatus.Finished;
            return;
        }

        await RetryPolicy.ExecuteAsync(
            action: (_, ct) => DownloadOnceAsync(ct),
            maxRetries: _settings.MaxRetries,
            baseDelay: TimeSpan.FromMilliseconds(_settings.RetryDelayMilliseconds),
            onRetry: (attempt, ex) =>
            {
                _retryCount = attempt;
                _lastError = ex.Message;
                _status = ConnectionStatus.SendingRequest;
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> DownloadOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await TransferAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (FtpRestartNotSupportedException)
        {
            // The chunk on disk can no longer be continued, and re-requesting the
            // same offset would be refused identically every time. Throwing the
            // partial away and fetching from zero is the only path that finishes,
            // so it happens here rather than costing a retry attempt.
            DiscardPartialChunk();
            await TransferAsync(cancellationToken).ConfigureAwait(false);
        }

        _status = ConnectionStatus.Finished;
        return true;
    }

    private async Task TransferAsync(CancellationToken cancellationToken)
    {
        _status = ConnectionStatus.SendingRequest;

        long start = RangeStart + BytesDownloaded;
        long remaining = RangeEnd - start + 1;
        if (remaining <= 0) return;

        await using var channel = await FtpControlChannel
            .ConnectAsync(_uri, _credentials, cancellationToken)
            .ConfigureAwait(false);

        var dataStream = await channel.OpenReadAsync(_path, start, cancellationToken).ConfigureAwait(false);

        _status = ConnectionStatus.ReceivingData;

        Directory.CreateDirectory(Path.GetDirectoryName(ChunkFilePath)!);

        int bufferSize = _settings.ClampBufferSize();

        // OpenOrCreate + seek rather than Append: the same file is reopened on
        // every resume and must not be truncated, and the offset is derived from
        // what is already in it.
        using var fileStream = new FileStream(
            ChunkFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
            bufferSize, useAsync: true);
        fileStream.Seek(start - RangeStart, SeekOrigin.Begin);

        var buffer = new byte[bufferSize];
        while (remaining > 0)
        {
            // Ask before reading, exactly as the HTTP connection does, so the
            // limiter caps what comes off the socket instead of sleeping off an
            // overshoot. The read is additionally clamped to what is left of the
            // segment — FTP would otherwise run on to the end of the file and
            // this chunk would overwrite its neighbour's bytes.
            int allowance = await _bandwidthLimiter.RequestAsync(buffer.Length, cancellationToken).ConfigureAwait(false);
            int wanted = (int)Math.Min(allowance, remaining);

            int bytesRead = wanted <= 0
                ? 0
                : await dataStream.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);

            if (bytesRead <= 0)
            {
                _bandwidthLimiter.Return(allowance);
                break;
            }

            _bandwidthLimiter.Return(allowance - bytesRead);

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _bytesDownloaded, bytesRead);
            remaining -= bytesRead;
        }

        await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // A stream that ended early left the segment short. Reported as a failure
        // so the retry policy gets a turn — silently accepting it would hand the
        // merger a chunk full of zeroes at the tail.
        if (remaining > 0 && !IsUnbounded)
            throw new IOException($"The FTP data connection ended {remaining} bytes early.");
    }

    private void DiscardPartialChunk()
    {
        Interlocked.Exchange(ref _bytesDownloaded, 0);
        try { if (File.Exists(ChunkFilePath)) File.Delete(ChunkFilePath); } catch { /* best-effort */ }
    }
}
