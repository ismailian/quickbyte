using System.Threading;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// Assembles the ordered chunk files produced by the connection pool into the
/// single, final destination file, then cleans up the temp chunks.
///
/// A merge is resumable. It deletes each chunk as soon as its bytes are safely
/// in the output — that is what keeps a large download from needing twice its
/// size in free space — and a merge that deletes as it goes cannot simply be
/// started again after an interruption. So it records its plan in the temp folder
/// before it changes anything, and a merge that finds one carries on from it.
/// </summary>
public interface IFileMerger
{
    /// <summary>
    /// Merges <paramref name="orderedChunkPaths"/> (all inside
    /// <paramref name="tempFolder"/>) into <paramref name="destinationFilePath"/>,
    /// or carries on the interrupted merge <paramref name="tempFolder"/> holds, in
    /// which case the chunk list is ignored — the plan already names them.
    /// </summary>
    /// <exception cref="Exceptions.InsufficientDiskSpaceException">
    /// The destination volume cannot hold what is left to merge. Nothing has been
    /// changed.
    /// </exception>
    Task MergeAsync(
        string tempFolder,
        IReadOnlyList<string> orderedChunkPaths,
        string destinationFilePath,
        int bufferSize,
        IProgress<long>? bytesMergedProgress,
        CancellationToken cancellationToken);

    /// <summary>
    /// Whether <paramref name="tempFolder"/> holds a merge that started and did
    /// not finish. Every byte of such a download is already on disk, so it needs
    /// no server to complete — and its chunks are no longer in the shape the pool
    /// would resume from.
    /// </summary>
    bool HasInterruptedMerge(string tempFolder);

    /// <summary>
    /// Forgets an interrupted merge: its plan, and the partly written file at
    /// <paramref name="destinationFilePath"/> if the plan was writing it there. The
    /// chunks are the pool's to discard.
    /// </summary>
    void DiscardInterruptedMerge(string tempFolder, string destinationFilePath);

    void CleanupChunks(IReadOnlyList<string> chunkPaths, string tempFolder);
}
