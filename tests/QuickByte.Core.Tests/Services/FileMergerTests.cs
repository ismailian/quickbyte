using QuickByte.Core.Exceptions;
using QuickByte.Core.Services;

namespace QuickByte.Core.Tests.Services;

/// <summary>
/// The last step, and the one where a mistake is permanent: each chunk is deleted
/// as soon as its bytes are in the output, so a merge that concatenates them in
/// the wrong order — or resumes from the wrong place after an interruption —
/// produces a corrupt file with nothing left to rebuild it from.
///
/// Most of the chunks here sit on the same volume as the destination, which is
/// the in-place path: appended onto the first chunk, then renamed. The copy path a
/// second volume takes is forced with the merger's own seam, since a test machine
/// cannot be relied on to have two.
/// </summary>
public sealed class FileMergerTests
{
    private const int BufferSize = 4096;

    private static FileMerger InPlace(long? freeBytes = null) => new(_ => freeBytes, (_, _) => true);

    private static FileMerger Copying(long? freeBytes = null) => new(_ => freeBytes, (_, _) => false);

    public static IEnumerable<object[]> BothWays() => new[] { new object[] { true }, new object[] { false } };

    private static FileMerger Merger(bool inPlace) => inPlace ? InPlace() : Copying();

    // --------------------------------------------------------- what it makes --

    [Theory]
    [MemberData(nameof(BothWays))]
    public async Task Chunks_are_concatenated_in_the_order_they_are_given(bool inPlace)
    {
        using var folder = new TempFolder();
        string a = folder.WriteFile("part0.tmp", new byte[] { 1, 2, 3 });
        string b = folder.WriteFile("part1.tmp", new byte[] { 4, 5 });
        string c = folder.WriteFile("part2.tmp", new byte[] { 6 });
        string destination = Path.Combine(folder.Path, "out", "merged.bin");

        await Merger(inPlace).MergeAsync(folder.Path, new[] { a, b, c }, destination, BufferSize, null, default);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, File.ReadAllBytes(destination));
    }

    [Theory]
    [MemberData(nameof(BothWays))]
    public async Task A_merge_spans_more_than_one_buffer(bool inPlace)
    {
        using var folder = new TempFolder();
        byte[] first = Enumerable.Range(0, 10_000).Select(i => (byte)(i % 251)).ToArray();
        byte[] second = Enumerable.Range(0, 7_000).Select(i => (byte)(i % 253)).ToArray();
        string a = folder.WriteFile("part0.tmp", first);
        string b = folder.WriteFile("part1.tmp", second);
        string destination = Path.Combine(folder.Path, "out", "merged.bin");

        await Merger(inPlace).MergeAsync(folder.Path, new[] { a, b }, destination, BufferSize, null, default);

        Assert.Equal(first.Concat(second).ToArray(), File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task An_empty_chunk_contributes_nothing_and_breaks_nothing()
    {
        using var folder = new TempFolder();
        string a = folder.WriteFile("part0.tmp", new byte[] { 1, 2 });
        string empty = folder.WriteFile("part1.tmp", Array.Empty<byte>());
        string c = folder.WriteFile("part2.tmp", new byte[] { 3 });
        string destination = Path.Combine(folder.Path, "out", "merged.bin");

        await InPlace().MergeAsync(folder.Path, new[] { a, empty, c }, destination, BufferSize, null, default);

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
    }

    [Theory]
    [MemberData(nameof(BothWays))]
    public async Task Merging_overwrites_a_file_that_is_already_there(bool inPlace)
    {
        using var folder = new TempFolder();
        string a = folder.WriteFile("part0.tmp", new byte[] { 9 });
        Directory.CreateDirectory(Path.Combine(folder.Path, "out"));
        string destination = Path.Combine(folder.Path, "out", "merged.bin");
        File.WriteAllBytes(destination, new byte[] { 1, 2, 3, 4, 5 });

        await Merger(inPlace).MergeAsync(folder.Path, new[] { a }, destination, BufferSize, null, default);

        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task The_destination_folder_is_created_if_it_is_missing()
    {
        using var folder = new TempFolder();
        string a = folder.WriteFile("part0.tmp", new byte[] { 1 });
        string destination = Path.Combine(folder.Path, "a", "b", "merged.bin");

        await InPlace().MergeAsync(folder.Path, new[] { a }, destination, BufferSize, null, default);

        Assert.True(File.Exists(destination));
    }

    // ----------------------------------------------------------- disk space --

    [Theory]
    [MemberData(nameof(BothWays))]
    public async Task Every_chunk_is_gone_once_its_bytes_are_in_the_file(bool inPlace)
    {
        // Deleting them all at the end is what made a download need twice its
        // size in free space for the length of the merge.
        using var folder = new TempFolder();
        string a = folder.WriteFile("part0.tmp", new byte[] { 1 });
        string b = folder.WriteFile("part1.tmp", new byte[] { 2 });
        string destination = Path.Combine(folder.Path, "out", "merged.bin");

        await Merger(inPlace).MergeAsync(folder.Path, new[] { a, b }, destination, BufferSize, null, default);

        Assert.False(File.Exists(a));
        Assert.False(File.Exists(b));
        Assert.False(File.Exists(folder.File(FileMerger.PlanFileName)));
    }

    [Fact]
    public async Task A_single_chunk_on_the_same_volume_is_renamed_rather_than_copied()
    {
        // A one-connection download of 40 GB used to spend minutes "merging" by
        // copying every byte. The creation time is the tell: a move keeps it, a
        // copy is a new file.
        using var folder = new TempFolder();
        string a = folder.WriteFile("part0.tmp", new byte[] { 1, 2, 3 });
        var created = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        File.SetCreationTimeUtc(a, created);
        string destination = Path.Combine(folder.Path, "out", "merged.bin");

        await InPlace().MergeAsync(folder.Path, new[] { a }, destination, BufferSize, null, default);

        Assert.Equal(created, File.GetCreationTimeUtc(destination));
    }

    [Fact]
    public async Task A_merge_in_place_needs_room_only_for_the_largest_chunk_it_appends()
    {
        using var folder = new TempFolder();
        string a = folder.WriteFile("part0.tmp", new byte[900]);
        string b = folder.WriteFile("part1.tmp", new byte[100]);
        string destination = Path.Combine(folder.Path, "out", "merged.bin");

        await InPlace(freeBytes: 100).MergeAsync(folder.Path, new[] { a, b }, destination, BufferSize, null, default);

        Assert.Equal(1000, new FileInfo(destination).Length);
    }

    [Fact]
    public async Task A_merge_that_will_not_fit_says_so_before_touching_anything()
    {
        using var folder = new TempFolder();
        string a = folder.WriteFile("part0.tmp", new byte[] { 1, 2, 3 });
        string b = folder.WriteFile("part1.tmp", new byte[] { 4, 5 });
        string destination = Path.Combine(folder.Path, "out", "merged.bin");

        var thrown = await Assert.ThrowsAsync<InsufficientDiskSpaceException>(() =>
            Copying(freeBytes: 4).MergeAsync(folder.Path, new[] { a, b }, destination, BufferSize, null, default));

        Assert.Equal(5, thrown.Shortfall.Needed);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(a));
        Assert.True(File.Exists(b));
        Assert.False(File.Exists(folder.File(FileMerger.PlanFileName)));
    }

    // ------------------------------------------------------ an interruption --

    [Theory]
    [MemberData(nameof(BothWays))]
    public async Task A_merge_cancelled_half_way_through_a_chunk_resumes_into_the_right_file(bool inPlace)
    {
        // The app exiting in the middle of a big merge. Chunks already merged are
        // gone, one is half appended, and the rest are untouched — only the plan
        // and the output's length say how far it got.
        using var folder = new TempFolder();
        byte[][] parts = Enumerable.Range(0, 3)
            .Select(p => Enumerable.Range(0, 5 * 1024 * 1024).Select(i => (byte)((i + p * 7) % 251)).ToArray())
            .ToArray();
        var paths = parts.Select((bytes, i) => folder.WriteFile($"part{i}.tmp", bytes)).ToArray();
        string destination = Path.Combine(folder.Path, "out", "merged.bin");

        using var cts = new CancellationTokenSource();
        var cancelOnFirstReport = new Inline(merged => { if (merged > 5 * 1024 * 1024 + 1) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Merger(inPlace).MergeAsync(folder.Path, paths, destination, 64 * 1024, cancelOnFirstReport, cts.Token));
        Assert.True(Merger(inPlace).HasInterruptedMerge(folder.Path));

        // Resumed the way DownloadService resumes one: no chunk list, the plan decides.
        await Merger(inPlace).MergeAsync(folder.Path, Array.Empty<string>(), destination, 64 * 1024, null, default);

        Assert.Equal(parts.SelectMany(p => p).ToArray(), File.ReadAllBytes(destination));
        Assert.False(Merger(inPlace).HasInterruptedMerge(folder.Path));
    }

    [Fact]
    public async Task A_chunk_whose_delete_was_interrupted_is_not_merged_twice()
    {
        // Flushed into the output, then the process died before the delete.
        using var folder = new TempFolder();
        folder.WriteFile("part0.tmp", new byte[] { 1, 2, 3, 4, 5 });
        folder.WriteFile("part1.tmp", new byte[] { 4, 5 });
        folder.WriteFile("part2.tmp", new byte[] { 6 });
        string destination = Path.Combine(folder.Path, "out", "merged.bin");
        FileMerger.WritePlan(folder.Path, Plan(destination, inPlace: true, ("part0.tmp", 3), ("part1.tmp", 2), ("part2.tmp", 1)));

        await InPlace().MergeAsync(folder.Path, Array.Empty<string>(), destination, BufferSize, null, default);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, File.ReadAllBytes(destination));
        Assert.False(File.Exists(folder.File("part1.tmp")));
    }

    [Fact]
    public async Task A_merge_interrupted_after_its_rename_just_finishes()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, "out"));
        string destination = Path.Combine(folder.Path, "out", "merged.bin");
        File.WriteAllBytes(destination, new byte[] { 1, 2, 3 });
        FileMerger.WritePlan(folder.Path, Plan(destination, inPlace: true, ("part0.tmp", 2), ("part1.tmp", 1)));

        await InPlace().MergeAsync(folder.Path, Array.Empty<string>(), destination, BufferSize, null, default);

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
        Assert.False(InPlace().HasInterruptedMerge(folder.Path));
    }

    [Fact]
    public async Task A_chunk_missing_from_a_resumed_merge_is_reported_and_the_plan_forgotten()
    {
        // Nothing to rebuild from. Saying so, and dropping the plan so the next
        // run downloads what is missing, beats failing identically forever.
        using var folder = new TempFolder();
        folder.WriteFile("part0.tmp", new byte[] { 1, 2 });
        string destination = Path.Combine(folder.Path, "out", "merged.bin");
        FileMerger.WritePlan(folder.Path, Plan(destination, inPlace: true, ("part0.tmp", 2), ("part1.tmp", 1)));

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            InPlace().MergeAsync(folder.Path, Array.Empty<string>(), destination, BufferSize, null, default));

        Assert.Contains("downloaded again", thrown.Message);
        Assert.False(InPlace().HasInterruptedMerge(folder.Path));
        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task A_plan_that_cannot_be_read_is_never_merged_into_an_empty_file()
    {
        using var folder = new TempFolder();
        folder.WriteFile(FileMerger.PlanFileName, "{ not a plan"u8.ToArray());
        string destination = Path.Combine(folder.Path, "out", "merged.bin");

        await Assert.ThrowsAsync<IOException>(() =>
            InPlace().MergeAsync(folder.Path, Array.Empty<string>(), destination, BufferSize, null, default));

        Assert.False(File.Exists(destination));
    }

    [Fact]
    public async Task A_plan_that_names_a_file_outside_its_folder_is_not_believed()
    {
        using var folder = new TempFolder();
        string outside = folder.WriteFile("outside.bin", new byte[] { 7 });
        string temp = Path.Combine(folder.Path, "temp");
        string destination = Path.Combine(folder.Path, "out", "merged.bin");
        FileMerger.WritePlan(temp, Plan(destination, inPlace: false, (@"..\outside.bin", 1)));

        await Assert.ThrowsAsync<IOException>(() =>
            Copying().MergeAsync(temp, Array.Empty<string>(), destination, BufferSize, null, default));

        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void Discarding_an_interrupted_copy_removes_the_half_file_it_was_writing()
    {
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, "out"));
        string destination = Path.Combine(folder.Path, "out", "merged.bin");
        File.WriteAllBytes(destination, new byte[] { 1 });
        FileMerger.WritePlan(folder.Path, Plan(destination, inPlace: false, ("part0.tmp", 1), ("part1.tmp", 1)));

        Copying().DiscardInterruptedMerge(folder.Path, destination);

        Assert.False(File.Exists(destination));
        Assert.False(Copying().HasInterruptedMerge(folder.Path));
    }

    [Fact]
    public void Discarding_never_deletes_a_file_the_download_does_not_name()
    {
        // The plan is read from disk; a path in it is not one to delete on its
        // word alone.
        using var folder = new TempFolder();
        string someoneElses = folder.WriteFile("important.docx", new byte[] { 1 });
        FileMerger.WritePlan(folder.Path, Plan(someoneElses, inPlace: false, ("part0.tmp", 1)));

        Copying().DiscardInterruptedMerge(folder.Path, Path.Combine(folder.Path, "out", "merged.bin"));

        Assert.True(File.Exists(someoneElses));
    }

    // ------------------------------------------------------------ progress --

    [Fact]
    public async Task Progress_ends_on_the_exact_total()
    {
        // The throttle can swallow the last report, and a bar that stops at 99.4%
        // reads as a stall.
        using var folder = new TempFolder();
        string a = folder.WriteFile("part0.tmp", new byte[5_000]);
        string b = folder.WriteFile("part1.tmp", new byte[3_000]);
        var reported = new List<long>();

        await InPlace().MergeAsync(
            folder.Path, new[] { a, b }, Path.Combine(folder.Path, "out", "merged.bin"), BufferSize,
            new Inline(reported.Add), default);

        Assert.Equal(8_000, reported[^1]);
    }

    [Fact]
    public async Task A_cancelled_merge_stops()
    {
        using var folder = new TempFolder();
        string a = folder.WriteFile("part0.tmp", new byte[1024]);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InPlace().MergeAsync(folder.Path, new[] { a }, folder.File("merged.bin"), BufferSize, null, cts.Token));
    }

    // -------------------------------------------------------------- cleanup --

    [Fact]
    public void CleanupChunks_removes_the_chunks_and_then_the_folder()
    {
        using var parent = new TempFolder();
        string chunkFolder = Path.Combine(parent.Path, "chunks");
        Directory.CreateDirectory(chunkFolder);
        string a = Path.Combine(chunkFolder, "part0.tmp");
        string b = Path.Combine(chunkFolder, "part1.tmp");
        File.WriteAllBytes(a, new byte[] { 1 });
        File.WriteAllBytes(b, new byte[] { 2 });

        new FileMerger().CleanupChunks(new[] { a, b }, chunkFolder);

        Assert.False(Directory.Exists(chunkFolder));
    }

    [Fact]
    public void CleanupChunks_leaves_a_folder_that_still_holds_something_else()
    {
        using var parent = new TempFolder();
        string chunkFolder = Path.Combine(parent.Path, "chunks");
        Directory.CreateDirectory(chunkFolder);
        string a = Path.Combine(chunkFolder, "part0.tmp");
        File.WriteAllBytes(a, new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(chunkFolder, "someone-elses.dat"), new byte[] { 2 });

        new FileMerger().CleanupChunks(new[] { a }, chunkFolder);

        Assert.True(Directory.Exists(chunkFolder));
        Assert.False(File.Exists(a));
    }

    [Fact]
    public void CleanupChunks_shrugs_at_a_file_that_is_already_gone()
    {
        using var folder = new TempFolder();

        // Best-effort throughout: a missing or locked chunk is not worth an error
        // dialog on a path the user has already finished with.
        new FileMerger().CleanupChunks(new[] { folder.File("never-existed.tmp") }, folder.Path);
    }

    // ------------------------------------------------------------- plumbing --

    private static FileMerger.Plan Plan(string destination, bool inPlace, params (string Name, long Length)[] chunks) => new()
    {
        Destination = destination,
        InPlace = inPlace,
        Chunks = chunks.Select(c => new FileMerger.PlanChunk(c.Name, c.Length)).ToList()
    };

    /// <summary>Reports on the reporting thread, in order — what DownloadService itself uses.</summary>
    private sealed class Inline : IProgress<long>
    {
        private readonly Action<long> _report;
        public Inline(Action<long> report) => _report = report;
        public void Report(long value) => _report(value);
    }
}
