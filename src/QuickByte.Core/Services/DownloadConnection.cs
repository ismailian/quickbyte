using System.Threading;
using System.Net;
using System.Net.Http;
using QuickByte.Core.Enums;
using QuickByte.Core.Exceptions;
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
    private readonly RequestOptions? _options;
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
        IBandwidthLimiter? bandwidthLimiter = null,
        RequestOptions? options = null)
    {
        _httpClient = httpClient;
        _options = options;
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

    /// <summary>
    /// True when this connection was handed no real end: the single-connection
    /// case for a file whose size the server never disclosed. It asks for an
    /// open-ended range and reads until the stream closes, so the short-transfer
    /// check at the end of a fetch has nothing to measure against.
    /// </summary>
    private bool IsUnbounded => RangeEnd == RangeSplitter.UnboundedEnd;

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

        // An open-ended "bytes=start-" when there is no known end. The sentinel
        // written out as a literal 9223372036854775806 is a range a fair number
        // of servers answer with 416 instead of the rest of the file.
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(
            start, IsUnbounded ? null : RangeEnd);
        HttpRequestDecorator.Apply(request, _options);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        // Separated from the generic failure below so RetryPolicy stops rather
        // than spending its attempts re-presenting the same rejected password.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ProxyAuthenticationRequired)
        {
            throw new AuthenticationRequiredException(
                _options?.HasCredentials == true
                    ? "The server rejected the supplied user name or password."
                    : "The server requires a user name and password.")
            { CredentialsWereSupplied = _options?.HasCredentials == true };
        }

        response.EnsureSuccessStatusCode();

        // 200 instead of 206 means the server ignored the Range header and is
        // answering with the whole file from byte zero — the HTTP twin of an FTP
        // server refusing REST, and every bit as quiet about it. Writing that
        // body at this segment's offset is precisely what corrupts a resumed or
        // segmented download, so it is recovered from here rather than trusted.
        bool rangeHonoured = response.StatusCode == HttpStatusCode.PartialContent;

        // A 206 that starts somewhere else is the same failure wearing the right
        // status code. Only checked when the server said where it started.
        if (rangeHonoured && response.Content.Headers.ContentRange?.From is long from && from != start)
        {
            throw new IOException(
                $"The server answered a request for byte {start} with a range starting at {from}.");
        }

        if (!rangeHonoured && RangeStart > 0)
        {
            // A later segment cannot be salvaged from a whole-file response: the
            // pool split this download on the strength of a probe that said
            // ranges were supported, and the bytes this connection owns are not
            // where this response begins.
            throw new IOException(
                $"The server ignored the request for bytes {start}-{RangeEnd} and answered with the whole file.");
        }

        // The first segment can be salvaged — the response starts exactly where
        // its chunk does. Throw the partial away and take the file from zero,
        // the same recovery FtpDownloadConnection performs when REST is refused.
        bool restartFromZero = !rangeHonoured && start > RangeStart;
        if (restartFromZero)
        {
            Interlocked.Exchange(ref _bytesDownloaded, 0);
            start = RangeStart;
        }

        _status = ConnectionStatus.ReceivingData;

        Directory.CreateDirectory(Path.GetDirectoryName(ChunkFilePath)!);

        int bufferSize = _settings.ClampBufferSize();

        // OpenOrCreate + seek rather than Append: the chunk is reopened on every
        // resume and must not be truncated, and the offset to carry on from is
        // derived from what is already in it.
        using var fileStream = new FileStream(
            ChunkFilePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read,
            bufferSize, useAsync: true);

        // Only when the partial was just disowned: an older, longer chunk would
        // otherwise leave a tail of stale bytes past the end of the new content.
        if (restartFromZero) fileStream.SetLength(0);
        fileStream.Seek(start - RangeStart, SeekOrigin.Begin);

        using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        long remaining = RangeEnd - start + 1;
        var buffer = new byte[bufferSize];
        while (remaining > 0)
        {
            // Ask before reading, not after: the allowance caps how much this
            // read may pull off the socket, so the connection never overshoots
            // the limit and then sleeps it off. With no limit configured this
            // returns the full buffer without touching a lock.
            int allowance = await _bandwidthLimiter.RequestAsync(buffer.Length, cancellationToken).ConfigureAwait(false);

            // Clamped to what is left of the segment as well, exactly as the FTP
            // connection does. A whole-file response accepted above — or a server
            // that simply sends more than it was asked for — would otherwise run
            // this chunk on over its neighbour's bytes.
            int wanted = (int)Math.Min(allowance, remaining);

            int bytesRead = wanted <= 0
                ? 0
                : await responseStream.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);

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

        // A stream that ended before the segment did leaves a short chunk, and
        // nothing downstream would notice: the pool sees a connection that ran to
        // the end of its loop, and the merge writes the hole into the final file.
        // Reported so RetryPolicy gets a turn — and because the bytes that did
        // arrive are still on disk, the retry carries on from them.
        if (remaining > 0 && !IsUnbounded)
            throw new IOException($"The connection ended {remaining} bytes early.");

        _status = ConnectionStatus.Finished;
        return true;
    }
}
