using System.Threading;
using QuickByte.Core.Interfaces;

namespace QuickByte.Core.Services;

/// <summary>
/// Merges the ordered chunk files written by each connection into the
/// single final file, then removes the temp chunks and their folder.
/// </summary>
public sealed class FileMerger : IFileMerger
{
    /// <summary>
    /// Merging runs at disk speed, so reporting every buffer would post
    /// thousands of marshaled UI updates per second and stall the very windows
    /// it is meant to inform. One report per this many merged bytes (plus a
    /// final exact one) is plenty for a smooth bar.
    /// </summary>
    private const long ProgressReportIntervalBytes = 4L * 1024 * 1024;

    public async Task MergeAsync(
        IReadOnlyList<string> orderedChunkPaths,
        string destinationFilePath,
        int bufferSize,
        IProgress<long>? bytesMergedProgress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationFilePath)!);

        long totalMerged = 0;
        long lastReported = 0;
        await using var destination = new FileStream(
            destinationFilePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);

        // One buffer for the whole merge rather than one per chunk: with 32
        // connections and an 80 KB buffer that was 32 large-object allocations
        // for no reason — nothing survives an iteration.
        var buffer = new byte[bufferSize];

        foreach (var chunkPath in orderedChunkPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var source = new FileStream(
                chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true);

            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                totalMerged += bytesRead;

                if (totalMerged - lastReported >= ProgressReportIntervalBytes)
                {
                    lastReported = totalMerged;
                    bytesMergedProgress?.Report(totalMerged);
                }
            }
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        bytesMergedProgress?.Report(totalMerged); // final, exact
    }

    public void CleanupChunks(IReadOnlyList<string> chunkPaths, string tempFolder)
    {
        foreach (var path in chunkPaths)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort cleanup, ignore locked/missing files */ }
        }

        try
        {
            if (Directory.Exists(tempFolder) && Directory.GetFileSystemEntries(tempFolder).Length == 0)
                Directory.Delete(tempFolder);
        }
        catch { /* best-effort */ }
    }
}
