using System.Diagnostics;
using System.Threading;
using QuickByte.Core.Interfaces;

namespace QuickByte.Core.Helpers;

/// <summary>
/// Token-bucket <see cref="IBandwidthLimiter"/>: allowance accrues at
/// <see cref="BytesPerSecond"/> and callers spend it, sleeping when the bucket
/// runs dry.
///
/// A bucket rather than a fixed sleep-per-read because the connections of one
/// download share a single instance — the bucket lets whichever connection is
/// ready next take the available bytes, so a slow mirror on one segment doesn't
/// idle the budget the others could be using.
///
/// The rate is mutable and read fresh on every request, which is what makes the
/// Speed Limiter tab apply to a transfer already in flight instead of only to
/// the next one.
/// </summary>
public sealed class BandwidthLimiter : IBandwidthLimiter
{
    /// <summary>
    /// How much unspent allowance the bucket may hold, expressed as seconds of
    /// transfer. Small enough that a paused-then-resumed download can't dump a
    /// huge burst on the link, large enough to absorb ordinary scheduling jitter.
    /// </summary>
    private const double BurstSeconds = 0.25;

    /// <summary>
    /// Floor on bucket capacity. Without it a very low limit would cap the
    /// bucket below one read's worth of bytes and every read would be split into
    /// a stream of tiny, syscall-heavy fragments.
    /// </summary>
    private const int MinimumCapacityBytes = 16 * 1024;

    /// <summary>
    /// Longest single sleep while waiting for allowance. Bounded so a rate
    /// change (or a cancellation) is picked up promptly rather than after a nap
    /// sized by the old rate.
    /// </summary>
    private const int MaximumWaitMilliseconds = 200;

    private readonly object _sync = new();
    private long _bytesPerSecond;
    private double _tokens;
    private long _lastRefillTimestamp = Stopwatch.GetTimestamp();

    /// <summary>
    /// The cap in bytes per second. <c>0</c> means unlimited, in which case
    /// requests short-circuit and cost nothing — the common case must not pay
    /// for the feature.
    /// </summary>
    public long BytesPerSecond
    {
        get => Interlocked.Read(ref _bytesPerSecond);
        set => Interlocked.Exchange(ref _bytesPerSecond, Math.Max(0, value));
    }

    public BandwidthLimiter(long bytesPerSecond = 0) => BytesPerSecond = bytesPerSecond;

    public async ValueTask<int> RequestAsync(int desiredBytes, CancellationToken cancellationToken)
    {
        if (desiredBytes <= 0) return 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            long rate = BytesPerSecond;
            if (rate <= 0) return desiredBytes;

            TimeSpan wait;
            lock (_sync)
            {
                Refill(rate);

                if (_tokens >= 1)
                {
                    int granted = (int)Math.Min(desiredBytes, _tokens);
                    _tokens -= granted;
                    return granted;
                }

                // Sleep only as long as it takes to accrue the one byte that
                // gets us back into the branch above.
                wait = TimeSpan.FromMilliseconds(
                    Math.Clamp((1 - _tokens) / rate * 1000.0, 1, MaximumWaitMilliseconds));
            }

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Return(int unusedBytes)
    {
        if (unusedBytes <= 0) return;

        long rate = BytesPerSecond;
        if (rate <= 0) return;

        lock (_sync)
        {
            _tokens = Math.Min(_tokens + unusedBytes, Capacity(rate));
        }
    }

    /// <summary>
    /// Credits the bucket for time elapsed since the last request. Capping at
    /// <see cref="Capacity"/> is what keeps a limiter that has been idle — or
    /// unlimited — from releasing a huge burst the moment a rate is set.
    /// </summary>
    private void Refill(long rate)
    {
        long now = Stopwatch.GetTimestamp();
        double elapsedSeconds = (now - _lastRefillTimestamp) / (double)Stopwatch.Frequency;
        _lastRefillTimestamp = now;

        if (elapsedSeconds <= 0) return;
        _tokens = Math.Min(_tokens + elapsedSeconds * rate, Capacity(rate));
    }

    private static double Capacity(long rate) => Math.Max(rate * BurstSeconds, MinimumCapacityBytes);
}

/// <summary>
/// Applies several limiters at once — in practice a download's own cap plus the
/// application-wide one — granting only what all of them allow.
///
/// The refund in <see cref="RequestAsync"/> is the whole point: asking limiter A
/// for 64 KB and then discovering limiter B will only allow 8 KB would otherwise
/// spend 64 KB of A's budget to move 8 KB, and a per-download limit would drag
/// the global one down with it.
/// </summary>
public sealed class CompositeBandwidthLimiter : IBandwidthLimiter
{
    private readonly IBandwidthLimiter[] _limiters;

    public CompositeBandwidthLimiter(params IBandwidthLimiter[] limiters) => _limiters = limiters;

    public async ValueTask<int> RequestAsync(int desiredBytes, CancellationToken cancellationToken)
    {
        int granted = desiredBytes;

        for (int i = 0; i < _limiters.Length; i++)
        {
            int allowed = await _limiters[i].RequestAsync(granted, cancellationToken).ConfigureAwait(false);
            if (allowed >= granted) continue;

            for (int j = 0; j < i; j++)
                _limiters[j].Return(granted - allowed);

            granted = allowed;
        }

        return granted;
    }

    public void Return(int unusedBytes)
    {
        foreach (var limiter in _limiters)
            limiter.Return(unusedBytes);
    }
}

/// <summary>
/// The no-op limiter used when a connection is built without one, so the read
/// loop in <see cref="Services.DownloadConnection"/> stays a single code path
/// instead of branching on null every buffer.
/// </summary>
public sealed class UnlimitedBandwidthLimiter : IBandwidthLimiter
{
    public static readonly UnlimitedBandwidthLimiter Instance = new();

    private UnlimitedBandwidthLimiter() { }

    public ValueTask<int> RequestAsync(int desiredBytes, CancellationToken cancellationToken) =>
        ValueTask.FromResult(desiredBytes);

    public void Return(int unusedBytes) { }
}
