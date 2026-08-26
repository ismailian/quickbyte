using QuickByte.Core.Services;

namespace QuickByte.Core.Tests.Services;

/// <summary>
/// The last step, and the one where a mistake is permanent: the chunks are
/// deleted immediately afterwards, so a merge that concatenates them in the
/// wrong order produces a corrupt file with nothing left to rebuild it from.
/// </summary>
public sealed class FileMergerTests
{
    private const int BufferSize = 4096;

    [Fact]
    public async Task Chunks_are_concatenated_in_the_order_they_are_given()
    {
        using var folder = new TempFolder();
        string a = folder.WriteFile("part0.tmp", new byte[] { 1, 2, 3 });
        string b = folder.WriteFile("part1.tmp", new byte[] { 4, 5 });
        string c = folder.WriteFile("part2.tmp", new byte[] { 6 });
        string destination = folder.File("merged.bin");

        await new FileMerger().MergeAsync(new[] { a, b, c }, destination, BufferSize, null, default);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6 }, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task A_merge_spans_more_than_one_buffer()
    {
        using var folder = new TempFolder();
        byte[] first = Enumerable.Range(0, 10_000).Select(i => (byte)(i % 251)).ToArray();
        byte[] second = Enumerable.Range(0, 7_000).Select(i => (byte)(i % 253)).ToArray();
        string a = folder.WriteFile("part0.tmp", first);
        string b = folder.WriteFile("part1.tmp", second);
        string destination = folder.File("merged.bin");

        await new FileMerger().MergeAsync(new[] { a, b }, destination, BufferSize, null, default);

        Assert.Equal(first.Concat(second).ToArray(), File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task An_empty_chunk_contributes_nothing_and_breaks_nothing()
    {
        using var folder = new TempFolder();
        string a = folder.WriteFile("part0.tmp", new byte[] { 1, 2 });
        string empty = folder.WriteFile("part1.tmp", Array.Empty<byte>());
        string c = folder.WriteFile("part2.tmp", new byte[] { 3 });
        string destination = folder.File("merged.bin");

        await new FileMerger().MergeAsync(new[] { a, empty, c }, destination, BufferSize, null, default);

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task Merging_overwrites_a_file_that_is_already_there()
    {
        using var folder = new TempFolder();
        string a = folder.WriteFile("part0.tmp", new byte[] { 9 });
        string destination = folder.WriteFile("merged.bin", new byte[] { 1, 2, 3, 4, 5 });

        await new FileMerger().MergeAsync(new[] { a }, destination, BufferSize, null, default);

        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(destination));
    }

    [Fact]
    public async Task The_destination_folder_is_created_if_it_is_missing()
    {
        using var folder = new TempFolder();
        string a = folder.WriteFile("part0.tmp", new byte[] { 1 });
        string destination = Path.Combine(folder.Path, "a", "b", "merged.bin");

        await new FileMerger().MergeAsync(new[] { a }, destination, BufferSize, null, default);

        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task Progress_ends_on_the_exact_total()
    {
        // The throttle can swallow the last report, and a bar that stops at 99.4%
        // reads as a stall.
        using var folder = new TempFolder();
        string a = folder.WriteFile("part0.tmp", new byte[5_000]);
        string b = folder.WriteFile("part1.tmp", new byte[3_000]);
        var reported = new List<long>();

        await new FileMerger().MergeAsync(
            new[] { a, b }, folder.File("merged.bin"), BufferSize,
            new Progress<long>(reported.Add), default);

        // Progress<T> posts asynchronously; the final value is what matters.
        await WaitFor(() => reported.Count > 0 && reported[^1] == 8_000);
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
            new FileMerger().MergeAsync(new[] { a }, folder.File("merged.bin"), BufferSize, null, cts.Token));
    }

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

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
    }
}
