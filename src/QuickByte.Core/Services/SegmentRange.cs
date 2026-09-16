using System.Threading;
using QuickByte.Core.Helpers;

namespace QuickByte.Core.Services;

/// <summary>
/// The byte range one connection owns, and the one rule that makes that range
/// safe to shorten while the connection is still reading into it.
///
/// Dynamic segmentation (see <see cref="ConnectionPoolManager"/>) hands the back
/// half of a slow connection's range to a new connection. That is only correct if
/// no byte ends up in two chunks: the old connection must stop exactly where the
/// new one starts, even though its response is still streaming bytes past that
/// point. So every read is <em>reserved</em> before it happens — the connection
/// says which bytes the read may write — and a shorter end is only accepted beyond
/// everything already written or reserved. A read under way is never cut in half,
/// and the next read is clamped to the new end.
///
/// Shared by <see cref="DownloadConnection"/> and
/// <see cref="Ftp.FtpDownloadConnection"/>, which both loop "reserve, read, write".
/// </summary>
internal sealed class SegmentRange
{
    private readonly object _lock = new();
    private long _end;
    private long _reservedEnd;

    public SegmentRange(long start, long end)
    {
        Start = start;
        _end = end;
        _reservedEnd = start - 1;
    }

    public long Start { get; }

    /// <summary>The last byte of the range. Can move earlier, never later.</summary>
    public long End => Interlocked.Read(ref _end);

    /// <summary>A range with no known end — see <see cref="RangeSplitter.UnboundedEnd"/>. Never shortened.</summary>
    public bool IsUnbounded => End == RangeSplitter.UnboundedEnd;

    public long Length => End - Start + 1;

    /// <summary>
    /// How many bytes, up to <paramref name="allowance"/>, the read that starts at
    /// <paramref name="position"/> may write — and reserves them against a
    /// concurrent shortening. Zero once the range is done.
    /// </summary>
    public int Reserve(long position, int allowance)
    {
        lock (_lock)
        {
            long left = _end - position + 1;
            int wanted = (int)Math.Max(0, Math.Min(allowance, left));
            _reservedEnd = position + wanted - 1;
            return wanted;
        }
    }

    /// <summary>Gives back whatever a read reserved and did not write; <paramref name="position"/> is the next byte to write.</summary>
    public void Release(long position)
    {
        lock (_lock) _reservedEnd = position - 1;
    }

    /// <summary>The last byte that is written or about to be: the earliest point a split may begin after.</summary>
    public long CommittedEnd(long position)
    {
        lock (_lock) return Math.Max(_reservedEnd, position - 1);
    }

    /// <summary>
    /// Ends the range at <paramref name="newEnd"/> if nothing written or reserved
    /// lies beyond it. Returns false, changing nothing, otherwise — and always for
    /// an unbounded range, which has no length to divide.
    /// </summary>
    public bool TryShrinkEnd(long newEnd, long position)
    {
        lock (_lock)
        {
            if (_end == RangeSplitter.UnboundedEnd || newEnd >= _end || newEnd < Start) return false;
            if (newEnd < Math.Max(_reservedEnd, position - 1)) return false;

            Interlocked.Exchange(ref _end, newEnd);
            return true;
        }
    }
}
