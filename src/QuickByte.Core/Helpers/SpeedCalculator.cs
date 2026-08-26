using System.Collections.Concurrent;

namespace QuickByte.Core.Helpers;

/// <summary>
/// Computes a smoothed transfer speed from a rolling window of
/// (timestamp, totalBytes) samples, avoiding the jitter you'd get from
/// naively dividing instantaneous delta-bytes by delta-time.
/// Not tied to any specific download — one instance per connection pool.
/// </summary>
public sealed class SpeedCalculator
{
    private readonly record struct Sample(DateTime Timestamp, long TotalBytes);

    private readonly ConcurrentQueue<Sample> _samples = new();
    private readonly TimeSpan _windowSize;

    public SpeedCalculator(TimeSpan? windowSize = null)
    {
        _windowSize = windowSize ?? TimeSpan.FromSeconds(5);
    }

    public void AddSample(long totalBytesDownloaded)
    {
        var now = DateTime.UtcNow;
        _samples.Enqueue(new Sample(now, totalBytesDownloaded));

        while (_samples.TryPeek(out var oldest) && now - oldest.Timestamp > _windowSize)
        {
            _samples.TryDequeue(out _);
        }
    }

    /// <summary>Bytes per second, smoothed over the configured window.</summary>
    public double GetSpeedBytesPerSecond()
    {
        // Length is read off the snapshot, not off the queue: the count can drop
        // between the two as AddSample prunes the window on another thread, and
        // this then indexes an array that is shorter than the check promised.
        var arr = _samples.ToArray();
        if (arr.Length < 2) return 0;

        var first = arr[0];
        var last = arr[^1];

        double seconds = (last.Timestamp - first.Timestamp).TotalSeconds;
        if (seconds <= 0) return 0;

        long deltaBytes = last.TotalBytes - first.TotalBytes;
        return deltaBytes <= 0 ? 0 : deltaBytes / seconds;
    }

    public static TimeSpan? EstimateTimeRemaining(long downloaded, long total, double speedBytesPerSecond)
    {
        if (speedBytesPerSecond <= 0.01 || total <= 0) return null;
        long remaining = Math.Max(0, total - downloaded);
        double secondsLeft = remaining / speedBytesPerSecond;
        if (double.IsInfinity(secondsLeft) || secondsLeft > TimeSpan.MaxValue.TotalSeconds) return null;
        return TimeSpan.FromSeconds(secondsLeft);
    }
}
