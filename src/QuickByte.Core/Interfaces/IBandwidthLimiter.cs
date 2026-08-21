using System.Threading;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// Gate a <see cref="IDownloadConnection"/> asks before every read, so a
/// download can be held to a chosen transfer rate.
///
/// The contract is "ask, then take what you were given" rather than "read, then
/// sleep": a connection requests permission for a buffer's worth of bytes,
/// receives an allowance that may be smaller, and reads at most that much. That
/// ordering is what lets several connections share one budget without any of
/// them overshooting it first and apologising afterwards.
///
/// Implementations must be safe to call from every connection of a download
/// concurrently.
/// </summary>
public interface IBandwidthLimiter
{
    /// <summary>
    /// Waits until at least one byte of allowance is available and returns how
    /// many bytes the caller may read, never more than
    /// <paramref name="desiredBytes"/>.
    /// </summary>
    ValueTask<int> RequestAsync(int desiredBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Hands back allowance that was granted but not used — a short read, or a
    /// stream that ended. Without this, a connection whose reads are habitually
    /// smaller than its buffer would burn budget it never spent and the download
    /// would settle well below the configured rate.
    /// </summary>
    void Return(int unusedBytes);
}
