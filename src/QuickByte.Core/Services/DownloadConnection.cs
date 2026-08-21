using System.Threading;
using System.Net.Http;
using QuickByte.Core.Enums;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// Downloads exactly one byte range [RangeStart, RangeEnd] into its own
/// temp chunk file using an HTTP Range request. Fully independent and
/// unaware of sibling connections — it only exposes its own thread-safe
/// state via properties, which the pool manager polls on a timer to build
/// aggregated progress (no per-byte event chatter reaches the UI).
/// </summary>
public sealed class DownloadConnection : IDownloadConnection
{
    private readonly HttpClient _httpClient;
    private readonly string _url;
    private readonly DownloadSettings _settings;
    private readonly IBandwidthLimiter _bandwidthLimiter;
    private long _bytesDownloaded;
    private int _retryCount;
    private volatile ConnectionStatus _status = ConnectionStatus.Idle;
    private volatile string? _lastError;

    public int ConnectionId { get; }
    public long RangeStart { get; }
    public long RangeEnd { get; }
    public string ChunkFilePath { get; }

    /// <summary>Bytes already present in the chunk file from a previous run (resume support).</summary>
    private readonly long _resumeOffset;

    public long BytesDownloaded => Interlocked.Read(ref _bytesDownloaded);
    public int RetryCount => _retryCount;
    public ConnectionStatus Status => _status;
    public string? LastError => _lastError;

    public DownloadConnection(
        HttpClient httpClient,
        int connectionId,
        string url,
        long rangeStart,
        long rangeEnd,
        long alreadyDownloaded,
        string chunkFilePath,
        DownloadSettings settings,
        IBandwidthLimiter? bandwidthLimiter = null)
    {
        _httpClient = httpClient;
        ConnectionId = connectionId;
        _url = url;
        RangeStart = rangeStart;
        RangeEnd = rangeEnd;
        ChunkFilePath = chunkFilePath;
        _settings = settings;
        _bandwidthLimiter = bandwidthLimiter ?? UnlimitedBandwidthLimiter.Instance;
        _resumeOffset = Math.Clamp(alreadyDownloaded, 0, TotalBytes);
        _bytesDownloaded = _resumeOffset;
    }

    private long TotalBytes => RangeEnd - RangeStart + 1;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (_bytesDownloaded >= TotalBytes)
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
        _status = ConnectionStatus.SendingRequest;

        long start = RangeStart + BytesDownloaded;

        using var request = new HttpRequestMessage(HttpMethod.Get, _url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, RangeEnd);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        _status = ConnectionStatus.ReceivingData;

        Directory.CreateDirectory(Path.GetDirectoryName(ChunkFilePath)!);

        // Open in read/write so we can seek to resume without truncating what we already have.
        using var fileStream = new FileStream(
            ChunkFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
            _settings.StreamBufferSizeBytes, useAsync: true);
        fileStream.Seek(start - RangeStart, SeekOrigin.Begin);

        using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        var buffer = new byte[_settings.StreamBufferSizeBytes];
        while (true)
        {
            // Ask before reading, not after: the allowance caps how much this
            // read may pull off the socket, so the connection never overshoots
            // the limit and then sleeps it off. With no limit configured this
            // returns the full buffer without touching a lock.
            int allowance = await _bandwidthLimiter.RequestAsync(buffer.Length, cancellationToken).ConfigureAwait(false);

            int bytesRead = await responseStream
                .ReadAsync(buffer.AsMemory(0, allowance), cancellationToken)
                .ConfigureAwait(false);

            if (bytesRead <= 0)
            {
                _bandwidthLimiter.Return(allowance);
                break;
            }

            _bandwidthLimiter.Return(allowance - bytesRead);

            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _bytesDownloaded, bytesRead);
        }

        await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        _status = ConnectionStatus.Finished;
        return true;
    }
}
