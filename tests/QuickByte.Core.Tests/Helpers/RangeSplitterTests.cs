using QuickByte.Core.Helpers;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// The split has one job that cannot be got wrong: every byte of the file must
/// be claimed by exactly one connection. A gap is a hole in the merged file and
/// an overlap is a chunk written over its neighbour's bytes, and neither shows
/// up as an error anywhere downstream.
/// </summary>
public sealed class RangeSplitterTests
{
    [Theory]
    [InlineData(1000, 1)]
    [InlineData(1000, 8)]
    [InlineData(1001, 8)]
    [InlineData(1007, 8)]
    [InlineData(1_000_000_007, 32)]
    [InlineData(3, 2)]
    public void Split_covers_every_byte_exactly_once(long total, int connections)
    {
        var ranges = RangeSplitter.Split(total, connections);

        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(total - 1, ranges[^1].End);

        for (int i = 1; i < ranges.Count; i++)
        {
            // Contiguous and non-overlapping: each range starts exactly one byte
            // after the previous one ends.
            Assert.Equal(ranges[i - 1].End + 1, ranges[i].Start);
        }

        Assert.Equal(total, ranges.Sum(r => r.End - r.Start + 1));
    }

    [Fact]
    public void Split_distributes_the_remainder_rather_than_dropping_it()
    {
        var ranges = RangeSplitter.Split(10, 4);

        // 10 / 4 leaves 2 over, so the first two ranges are one byte longer.
        Assert.Equal(new[] { 3L, 3L, 2L, 2L }, ranges.Select(r => r.End - r.Start + 1).ToArray());
    }

    [Fact]
    public void Split_never_opens_more_connections_than_there_are_bytes()
    {
        var ranges = RangeSplitter.Split(3, 8);

        Assert.Equal(3, ranges.Count);
        Assert.All(ranges, r => Assert.Equal(1, r.End - r.Start + 1));
    }

    [Fact]
    public void Split_of_a_single_byte_is_one_range()
    {
        var ranges = RangeSplitter.Split(1, 8);

        Assert.Single(ranges);
        Assert.Equal(0, ranges[0].Start);
        Assert.Equal(0, ranges[0].End);
    }

    [Theory]
    [InlineData(0, 4)]
    [InlineData(-1, 4)]
    public void Split_rejects_a_length_it_cannot_divide(long total, int connections) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RangeSplitter.Split(total, connections));

    [Theory]
    [InlineData(1000, 0)]
    [InlineData(1000, -1)]
    public void Split_rejects_a_connection_count_below_one(long total, int connections) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => RangeSplitter.Split(total, connections));

    [Fact]
    public void UnboundedEnd_leaves_room_for_the_inclusive_end_to_be_incremented()
    {
        // Connections compute RangeEnd - start + 1. long.MaxValue itself would
        // overflow that; the sentinel is one below it for exactly that reason.
        Assert.Equal(long.MaxValue - 1, RangeSplitter.UnboundedEnd);
        Assert.Equal(long.MaxValue, RangeSplitter.UnboundedEnd - 0 + 1);
    }
}
