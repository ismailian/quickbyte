namespace QuickByte.Core.Helpers;

/// <summary>
/// Splits a total content length into N contiguous, near-equal byte ranges
/// (each connection gets one). Remainder bytes are distributed to the last
/// ranges so every byte is covered exactly once.
/// </summary>
public static class RangeSplitter
{
    /// <summary>
    /// The end of a range that has no known end — the single connection given to
    /// a file whose size the server never disclosed.
    ///
    /// It lives here, with the rest of the engine's range vocabulary, because
    /// three places have to agree on it: the pool invents it, and both the HTTP
    /// and FTP connections have to recognise it to know that "the stream ended"
    /// is a completed segment rather than a truncated one. Spelled out as
    /// <c>long.MaxValue - 1</c> in each of them, it was a magic number that had
    /// to be kept in step by hand.
    /// </summary>
    public const long UnboundedEnd = long.MaxValue - 1;

    public readonly record struct Range(long Start, long End);

    public static IReadOnlyList<Range> Split(long totalBytes, int connectionsCount)
    {
        if (totalBytes <= 0) throw new ArgumentOutOfRangeException(nameof(totalBytes));
        if (connectionsCount < 1) throw new ArgumentOutOfRangeException(nameof(connectionsCount));

        // Never open more connections than there are bytes to fetch.
        connectionsCount = (int)Math.Min(connectionsCount, totalBytes);

        var ranges = new List<Range>(connectionsCount);
        long chunkSize = totalBytes / connectionsCount;
        long remainder = totalBytes % connectionsCount;
        long start = 0;

        for (int i = 0; i < connectionsCount; i++)
        {
            long size = chunkSize + (i < remainder ? 1 : 0);
            long end = start + size - 1;
            ranges.Add(new Range(start, end));
            start = end + 1;
        }

        return ranges;
    }
}
