using System.Threading;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// Concatenates ordered chunk files produced by the connection pool into the
/// single, final destination file, then cleans up the temp chunks.
/// </summary>
public interface IFileMerger
{
    Task MergeAsync(
        IReadOnlyList<string> orderedChunkPaths,
        string destinationFilePath,
        int bufferSize,
        IProgress<long>? bytesMergedProgress,
        CancellationToken cancellationToken);

    void CleanupChunks(IReadOnlyList<string> chunkPaths, string tempFolder);
}
