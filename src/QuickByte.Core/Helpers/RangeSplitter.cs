namespace QuickByte.Core.Helpers;

/// <summary>
/// Splits a total content length into N contiguous, near-equal byte ranges
/// (each connection gets one). Remainder bytes are distributed to the last
/// ranges so every byte is covered exactly once.
/// </summary>
public static class RangeSplitter
{
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
