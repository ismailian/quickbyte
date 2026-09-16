namespace QuickByte.Core.Helpers;

/// <summary>A volume that cannot hold what a download still has to write to it.</summary>
public readonly record struct SpaceShortfall(string Volume, long Needed, long Available);

/// <summary>
/// Free space, and whether a download will fit in it.
///
/// A download that runs out of disk does not fail where it could have been
/// stopped — it fails at the end. The chunks fill the temp volume over hours,
/// and then the merge needs room for the finished file as well, so a 40 GB
/// download on a drive with 50 GB free reached 100% and died in the merge. That
/// is the one moment a disk-full error costs the most, and the numbers to predict
/// it were known before the first byte.
/// </summary>
public static class DiskSpace
{
    /// <summary>
    /// Free bytes on the volume holding <paramref name="path"/>, or null when that
    /// cannot be told — a UNC share, a drive that is not ready, a path that does
    /// not parse. Null is "unknown", and a check that cannot run lets the download
    /// go ahead rather than refusing it.
    /// </summary>
    public static long? AvailableBytes(string path)
    {
        try
        {
            string? root = VolumeOf(path);
            if (root is null) return null;

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The volume root a path lives on (<c>C:\</c>), or null for anything
    /// <see cref="DriveInfo"/> cannot describe, UNC paths included.
    /// </summary>
    public static string? VolumeOf(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            return root.ToUpperInvariant();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether two paths are on the same drive, so a file can move between them by
    /// renaming rather than copying. False whenever either side is unknown — the
    /// copy is always correct, only slower.
    /// </summary>
    public static bool SameVolume(string a, string b) =>
        VolumeOf(a) is { } left && string.Equals(left, VolumeOf(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first volume that cannot hold what is still to be written to it, or
    /// null when everything fits (or nothing could be measured). Needs on the same
    /// volume are added together: the temp folder and the destination on one drive
    /// compete for the same free space.
    /// </summary>
    public static SpaceShortfall? FindShortfall(
        IEnumerable<(string Path, long Bytes)> needs, Func<string, long?> available)
    {
        var byVolume = new Dictionary<string, (string Path, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, bytes) in needs)
        {
            if (bytes <= 0 || VolumeOf(path) is not { } volume) continue;
            byVolume[volume] = byVolume.TryGetValue(volume, out var sum) ? (sum.Path, sum.Bytes + bytes) : (path, bytes);
        }

        foreach (var (volume, need) in byVolume)
        {
            if (available(need.Path) is long free && free < need.Bytes)
                return new SpaceShortfall(volume, need.Bytes, free);
        }
        return null;
    }

    /// <summary>
    /// What a download still has to find room for, volume by volume, before it
    /// can finish: the bytes not yet fetched into the temp folder, and the room
    /// the merge needs at the destination.
    /// </summary>
    /// <param name="largestChunk">
    /// The biggest segment. A merge on the same volume appends every chunk onto
    /// the first and deletes each one as it goes, so beyond the chunks themselves
    /// it only ever needs room for the one being appended. On another volume the
    /// whole file is written there.
    /// </param>
    public static IEnumerable<(string Path, long Bytes)> StillNeeded(
        string tempFolder, string destinationFolder, long totalBytes, long downloadedBytes, long largestChunk, int chunkCount)
    {
        if (totalBytes <= 0) yield break;

        yield return (tempFolder, Math.Max(0, totalBytes - downloadedBytes));

        long forTheMerge = SameVolume(tempFolder, destinationFolder)
            ? (chunkCount > 1 ? largestChunk : 0)
            : totalBytes;
        yield return (destinationFolder, forTheMerge);
    }
}
