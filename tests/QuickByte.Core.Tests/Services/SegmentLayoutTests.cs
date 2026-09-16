using QuickByte.Core.Helpers;
using QuickByte.Core.Services;

namespace QuickByte.Core.Tests.Services;

/// <summary>
/// The two rules dynamic segmentation stands on. A connection's range may only be
/// shortened beyond every byte it has written or reserved (<see cref="SegmentRange"/>),
/// and the record of which chunk holds which bytes must describe the whole file
/// exactly once (<see cref="SegmentMap"/>). Get either wrong and two chunks hold
/// the same bytes, or none does, and the merge writes that into the file.
/// </summary>
public sealed class SegmentRangeTests
{
    [Fact]
    public void A_read_is_clamped_to_what_is_left_and_reserved()
    {
        var range = new SegmentRange(100, 199);

        Assert.Equal(64, range.Reserve(position: 100, allowance: 64));
        Assert.Equal(163, range.CommittedEnd(position: 100));

        Assert.Equal(36, range.Reserve(position: 164, allowance: 64));
        Assert.Equal(0, range.Reserve(position: 200, allowance: 64));
    }

    [Fact]
    public void A_range_cannot_be_shortened_into_a_read_under_way()
    {
        var range = new SegmentRange(0, 999);
        range.Reserve(position: 100, allowance: 50); // bytes 100..149 may be arriving right now

        Assert.False(range.TryShrinkEnd(newEnd: 120, position: 100));
        Assert.False(range.TryShrinkEnd(newEnd: 148, position: 100));
        Assert.True(range.TryShrinkEnd(newEnd: 149, position: 100));
        Assert.Equal(149, range.End);
    }

    [Fact]
    public void A_range_cannot_be_shortened_into_bytes_already_written()
    {
        var range = new SegmentRange(0, 999);
        range.Release(position: 300); // 0..299 on disk, nothing reserved

        Assert.False(range.TryShrinkEnd(newEnd: 298, position: 300));
        Assert.True(range.TryShrinkEnd(newEnd: 299, position: 300));
    }

    [Fact]
    public void A_range_only_ever_gets_shorter()
    {
        var range = new SegmentRange(0, 999);

        Assert.False(range.TryShrinkEnd(newEnd: 999, position: 0));
        Assert.False(range.TryShrinkEnd(newEnd: 5000, position: 0));
        Assert.False(range.TryShrinkEnd(newEnd: -1, position: 0));
    }

    [Fact]
    public void A_range_with_no_known_end_is_never_shortened()
    {
        var range = new SegmentRange(0, RangeSplitter.UnboundedEnd);

        Assert.False(range.TryShrinkEnd(newEnd: 1000, position: 0));
        Assert.True(range.IsUnbounded);
    }

    [Fact]
    public void The_next_read_after_a_shortening_stops_at_the_new_end()
    {
        var range = new SegmentRange(0, 999);
        range.Release(position: 10);
        range.TryShrinkEnd(newEnd: 19, position: 10);

        Assert.Equal(10, range.Reserve(position: 10, allowance: 64));
    }
}

public sealed class SegmentMapTests
{
    private static SegmentMap Map(long total, params (int Chunk, long Start, long End)[] segments) => new()
    {
        TotalBytes = total,
        Segments = segments.Select(s => new SegmentMap.Segment(s.Chunk, s.Start, s.End)).ToList()
    };

    [Fact]
    public void A_layout_that_covers_every_byte_once_describes_the_file_in_any_order()
    {
        Assert.True(Map(100, (0, 0, 49), (2, 75, 99), (1, 50, 74)).Describes(100));
    }

    [Theory]
    [InlineData(101)]
    [InlineData(99)]
    public void A_layout_for_another_length_does_not_describe_the_file(long total)
    {
        Assert.False(Map(100, (0, 0, 49), (1, 50, 99)).Describes(total));
    }

    [Fact]
    public void A_gap_an_overlap_or_a_chunk_named_twice_is_not_a_layout()
    {
        Assert.False(Map(100, (0, 0, 48), (1, 50, 99)).Describes(100));
        Assert.False(Map(100, (0, 0, 50), (1, 50, 99)).Describes(100));
        Assert.False(Map(100, (0, 0, 49), (0, 50, 99)).Describes(100));
        Assert.False(Map(100).Describes(100));
    }

    [Fact]
    public void Splitting_ends_one_segment_and_adds_the_rest_as_a_new_chunk()
    {
        var map = Map(100, (0, 0, 49), (1, 50, 99));

        var split = map.WithSplit(1, 74, new SegmentMap.Segment(map.NextChunk, 75, 99));

        Assert.True(split.Describes(100));
        Assert.Equal(2, split.Segments.Single(s => s.Start == 75).Chunk);
        Assert.Equal(74, split.Segments.Single(s => s.Chunk == 1).End);
    }

    [Fact]
    public void A_layout_is_written_and_read_back_whole()
    {
        using var folder = new TempFolder();
        var map = Map(100, (0, 0, 49), (3, 50, 99));

        SegmentMap.Write(folder.Path, map);
        var read = SegmentMap.Read(folder.Path)!;

        Assert.Equal(map.TotalBytes, read.TotalBytes);
        Assert.Equal(map.Segments, read.Segments);
        Assert.False(File.Exists(folder.File(SegmentMap.FileName + ".pending")));
    }

    [Fact]
    public void An_unreadable_layout_reads_as_none()
    {
        using var folder = new TempFolder();
        folder.WriteFile(SegmentMap.FileName, "{ half a layout"u8.ToArray());

        Assert.Null(SegmentMap.Read(folder.Path));
    }
}
