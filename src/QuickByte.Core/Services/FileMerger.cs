using System.Text.Json;
using System.Threading;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;

namespace QuickByte.Core.Services;

/// <summary>
/// Merges the ordered chunk files written by each connection into the single
/// final file, deleting each chunk as soon as its bytes are safely in the output.
/// </summary>
/// <remarks>
/// <para><b>Why it deletes as it goes.</b> Merging used to copy every chunk into a
/// new file and delete them all at the end, so for the length of the merge a
/// download occupied twice its size. A 40 GB file on a drive with 50 GB free
/// downloaded to 100% and then failed in the merge — the most expensive moment
/// there is to run out of disk.</para>
///
/// <para><b>Why it merges in place.</b> When the temp folder and the destination
/// share a volume, the chunks are appended onto the first one, which is then
/// renamed into place. Beyond the chunks themselves the merge needs room for only
/// the chunk being appended, and a single-connection download's "merge" is a
/// rename — it used to copy the whole file. On another volume the chunks are
/// copied into the destination instead, still deleting each one as it lands.</para>
///
/// <para><b>Why it keeps a plan.</b> A merge that deletes its inputs cannot be
/// started over after an interruption — the app exiting half way through a 40 GB
/// merge is ordinary. So before it changes anything it writes
/// <see cref="PlanFileName"/>: the chunks in order, with their lengths. The
/// output's length then says exactly how far it got: every chunk whose end it
/// reaches is merged (and deleted, or about to be), and anything past the last
/// such end is a partial append, cut off and done again. Each chunk is flushed to
/// disk before it is deleted, so the bytes are never only in a write cache.</para>
/// </remarks>
public sealed class FileMerger : IFileMerger
{
    internal const string PlanFileName = "merge.json";

    /// <summary>
    /// Merging runs at disk speed, so reporting every buffer would post
    /// thousands of marshaled UI updates per second and stall the very windows
    /// it is meant to inform. One report per this many merged bytes (plus a
    /// final exact one) is plenty for a smooth bar.
    /// </summary>
    private const long ProgressReportIntervalBytes = 4L * 1024 * 1024;

    private readonly Func<string, long?> _availableBytes;
    private readonly Func<string, string, bool> _sameVolume;

    public FileMerger() : this(DiskSpace.AvailableBytes, DiskSpace.SameVolume) { }

    /// <summary>
    /// For the tests: how much space a volume has, and whether two paths share one,
    /// are the two facts about the machine this class acts on, and neither can be
    /// arranged on demand.
    /// </summary>
    internal FileMerger(Func<string, long?> availableBytes, Func<string, string, bool> sameVolume)
    {
        _availableBytes = availableBytes;
        _sameVolume = sameVolume;
    }

    public bool HasInterruptedMerge(string tempFolder) =>
        !string.IsNullOrWhiteSpace(tempFolder) && File.Exists(PlanPath(tempFolder));

    public void DiscardInterruptedMerge(string tempFolder, string destinationFilePath)
    {
        if (!HasInterruptedMerge(tempFolder)) return;

        // Only ever the file this download names. The plan is read from disk, and
        // a path read from a file is not one to delete on that file's word alone.
        if (ReadPlan(tempFolder) is { InPlace: false } plan && SamePath(plan.Destination, destinationFilePath))
            TryDelete(destinationFilePath);

        TryDelete(PlanPath(tempFolder));
    }

    public async Task MergeAsync(
        string tempFolder,
        IReadOnlyList<string> orderedChunkPaths,
        string destinationFilePath,
        int bufferSize,
        IProgress<long>? bytesMergedProgress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var plan = ReadPlan(tempFolder);
        if (plan is not null && !SamePath(plan.Destination, destinationFilePath))
            throw Broken(tempFolder, destinationFilePath);

        // Nothing to merge from: a plan file that could not be read, or a resume
        // that brought no chunks. Starting a fresh merge of nothing would produce
        // an empty file and call it finished.
        if (plan is null && (HasInterruptedMerge(tempFolder) || orderedChunkPaths.Count == 0))
            throw Broken(tempFolder, destinationFilePath);

        plan ??= Begin(tempFolder, orderedChunkPaths, destinationFilePath);

        long total = plan.Chunks.Sum(c => c.Length);
        string target = plan.InPlace ? Path.Combine(tempFolder, plan.Chunks[0].Name) : plan.Destination;

        if (plan.InPlace && !File.Exists(target))
        {
            // Interrupted after the rename and before the plan was deleted: the
            // file is already where it belongs.
            if (File.Exists(plan.Destination) && new FileInfo(plan.Destination).Length == total)
            {
                TryDelete(PlanPath(tempFolder));
                bytesMergedProgress?.Report(total);
                return;
            }
            throw Broken(tempFolder, destinationFilePath);
        }

        long merged;
        await using (var output = new FileStream(
                         target, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, bufferSize, useAsync: true))
        {
            int next = ResumePoint(plan, output.Length, out merged);
            if (next < 0) throw Broken(tempFolder, destinationFilePath);

            // A partial append past the last whole chunk is cut off and done again.
            output.SetLength(merged);
            output.Seek(merged, SeekOrigin.Begin);

            // Chunks already in the output whose delete was interrupted.
            for (int i = plan.InPlace ? 1 : 0; i < next; i++)
                TryDelete(Path.Combine(tempFolder, plan.Chunks[i].Name));

            for (int i = next; i < plan.Chunks.Count; i++)
            {
                var chunk = new FileInfo(Path.Combine(tempFolder, plan.Chunks[i].Name));
                if (!chunk.Exists || chunk.Length != plan.Chunks[i].Length)
                    throw Broken(tempFolder, destinationFilePath);
            }

            EnsureRoom(plan, next, merged, target);

            bytesMergedProgress?.Report(merged);
            long lastReported = merged;

            // One buffer for the whole merge rather than one per chunk: with 32
            // connections and an 80 KB buffer that was 32 large-object allocations
            // for no reason — nothing survives an iteration.
            var buffer = new byte[bufferSize];

            for (int i = next; i < plan.Chunks.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string chunkPath = Path.Combine(tempFolder, plan.Chunks[i].Name);

                await using (var source = new FileStream(
                                 chunkPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize, useAsync: true))
                {
                    int bytesRead;
                    while ((bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                        merged += bytesRead;

                        if (merged - lastReported >= ProgressReportIntervalBytes)
                        {
                            lastReported = merged;
                            bytesMergedProgress?.Report(merged);
                        }
                    }
                }

                // Durable before the only other copy of these bytes is deleted.
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                TryDelete(chunkPath);
            }
        }

        if (plan.InPlace)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.Destination)!);
            File.Move(target, plan.Destination, overwrite: true);
        }

        TryDelete(PlanPath(tempFolder));
        bytesMergedProgress?.Report(merged); // final, exact
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

    // ------------------------------------------------------------ the plan --

    /// <summary>Records what is about to be merged, before anything is changed.</summary>
    private Plan Begin(string tempFolder, IReadOnlyList<string> orderedChunkPaths, string destinationFilePath)
    {
        string destination = Path.GetFullPath(destinationFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        var chunks = new List<PlanChunk>(orderedChunkPaths.Count);
        foreach (string path in orderedChunkPaths)
        {
            if (!SamePath(Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty, tempFolder))
                throw new ArgumentException($"Chunk '{path}' is not inside '{tempFolder}'.", nameof(orderedChunkPaths));

            chunks.Add(new PlanChunk(Path.GetFileName(path), new FileInfo(path).Length));
        }

        var plan = new Plan
        {
            Destination = destination,
            InPlace = chunks.Count > 0 && _sameVolume(tempFolder, destination),
            Chunks = chunks
        };

        EnsureRoom(plan, plan.InPlace ? 1 : 0, 0, destination);

        // On another volume the output is the destination itself, so a file
        // already there would read as merged bytes on a resume. Cleared before the
        // plan exists, never after.
        if (!plan.InPlace)
            using (new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None)) { }

        WritePlan(tempFolder, plan);
        return plan;
    }

    /// <summary>
    /// The first chunk still to merge, given what is already in the output, and the
    /// length of output that belongs to the chunks before it. -1 when the output is
    /// shorter than a merge in place could ever leave it: its first chunk was
    /// complete when the plan was written.
    /// </summary>
    private static int ResumePoint(Plan plan, long outputLength, out long merged)
    {
        merged = 0;
        int next = 0;
        foreach (var chunk in plan.Chunks)
        {
            if (merged + chunk.Length > outputLength) break;
            merged += chunk.Length;
            next++;
        }

        return plan.InPlace && next == 0 ? -1 : next;
    }

    private void EnsureRoom(Plan plan, int next, long merged, string target)
    {
        // In place, only the chunk being appended is ever held twice; elsewhere the
        // rest of the file has to fit at the destination.
        long needed = plan.InPlace
            ? plan.Chunks.Skip(next).Select(c => c.Length).DefaultIfEmpty(0).Max()
            : plan.Chunks.Sum(c => c.Length) - merged;

        if (DiskSpace.FindShortfall(new[] { (target, needed) }, _availableBytes) is { } shortfall)
            throw new InsufficientDiskSpaceException(shortfall);
    }

    /// <summary>
    /// A merge whose inputs are not what the plan says. Nothing can be salvaged by
    /// trying again, so the plan is forgotten and the next run downloads what is
    /// missing — the pool recognises a first chunk that grew past its range and
    /// starts the set over.
    /// </summary>
    private IOException Broken(string tempFolder, string destinationFilePath)
    {
        DiscardInterruptedMerge(tempFolder, destinationFilePath);
        return new IOException("Part of this download is no longer on disk, so it has to be downloaded again. Click Retry.");
    }

    private static string PlanPath(string tempFolder) => Path.Combine(tempFolder, PlanFileName);

    private static Plan? ReadPlan(string tempFolder)
    {
        try
        {
            string path = PlanPath(tempFolder);
            if (!File.Exists(path)) return null;

            var plan = JsonSerializer.Deserialize<Plan>(File.ReadAllText(path));
            return plan is { Chunks.Count: > 0 } && plan.Chunks.All(IsSafeChunk) ? plan : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A chunk name that is a bare file name with a real length, not a path out of the folder.</summary>
    private static bool IsSafeChunk(PlanChunk chunk) =>
        chunk.Length >= 0
        && !string.IsNullOrWhiteSpace(chunk.Name)
        && chunk.Name == Path.GetFileName(chunk.Name);

    /// <summary>Written beside the plan and renamed over it, so a crash never leaves half a plan.</summary>
    internal static void WritePlan(string tempFolder, Plan plan)
    {
        Directory.CreateDirectory(tempFolder);
        string path = PlanPath(tempFolder);
        string pending = path + ".pending";

        using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, plan);
            stream.Flush(flushToDisk: true);
        }
        File.Move(pending, path, overwrite: true);
    }

    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best-effort — a later pass or the sweep collects it */ }
    }

    internal sealed record Plan
    {
        public string Destination { get; init; } = string.Empty;

        /// <summary>Appending onto the first chunk and renaming it, rather than copying into the destination.</summary>
        public bool InPlace { get; init; }

        public List<PlanChunk> Chunks { get; init; } = new();
    }

    internal sealed record PlanChunk(string Name, long Length);
}
