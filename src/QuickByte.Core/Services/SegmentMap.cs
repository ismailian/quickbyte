using System.Text.Json;
using QuickByte.Core.Helpers;

namespace QuickByte.Core.Services;

/// <summary>
/// Which chunk file holds which bytes of a download — persisted as
/// <see cref="FileName"/> in its temp folder.
///
/// Resume used to be able to work this out: the split was always
/// <see cref="RangeSplitter.Split"/> of the file into N, so <c>part3.tmp</c> could
/// only be the head of the fourth range. Dynamic segmentation breaks that — a slow
/// range is halved mid-download and its back half given a chunk of its own — so
/// the layout is written down, and resume reads it instead of re-deriving it.
/// A download without a map (every download from before this existed) is exactly
/// the fixed split, and gets a map the first time it runs.
/// </summary>
internal sealed record SegmentMap
{
    public const string FileName = "segments.json";

    /// <summary>More chunk files than this and splitting stops: each one is a file handle and a merge step.</summary>
    public const int MaxSegments = 256;

    public long TotalBytes { get; init; }

    public List<Segment> Segments { get; init; } = new();

    public sealed record Segment(int Chunk, long Start, long End)
    {
        public long Length => End - Start + 1;
    }

    public static SegmentMap FromRanges(long totalBytes, IReadOnlyList<RangeSplitter.Range> ranges) => new()
    {
        TotalBytes = totalBytes,
        Segments = ranges.Select((range, i) => new Segment(i, range.Start, range.End)).ToList()
    };

    public int NextChunk => Segments.Count == 0 ? 0 : Segments.Max(s => s.Chunk) + 1;

    /// <summary>
    /// Whether this map describes a file of <paramref name="totalBytes"/>: every
    /// byte in exactly one segment, each chunk named once. Anything else — a server
    /// that now reports another length, a hand-edited or half-written file — is
    /// not a layout to resume from.
    /// </summary>
    public bool Describes(long totalBytes)
    {
        if (totalBytes <= 0 || TotalBytes != totalBytes) return false;
        if (Segments.Count == 0 || Segments.Count > MaxSegments) return false;
        if (Segments.Select(s => s.Chunk).Distinct().Count() != Segments.Count) return false;
        if (Segments.Any(s => s.Chunk < 0 || s.Start > s.End)) return false;

        long next = 0;
        foreach (var segment in Segments.OrderBy(s => s.Start))
        {
            if (segment.Start != next) return false;
            next = segment.End + 1;
        }
        return next == totalBytes;
    }

    /// <summary>The same layout with <paramref name="chunk"/> ended at <paramref name="newEnd"/> and the rest given to <paramref name="added"/>.</summary>
    public SegmentMap WithSplit(int chunk, long newEnd, Segment added) => this with
    {
        Segments = Segments
            .Select(s => s.Chunk == chunk ? s with { End = newEnd } : s)
            .Append(added)
            .ToList()
    };

    public static SegmentMap? Read(string tempFolder)
    {
        try
        {
            string path = Path.Combine(tempFolder, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize<SegmentMap>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null; // unreadable reads as absent: the fixed split, checked against the chunks
        }
    }

    /// <summary>Written beside the map and renamed over it, so a crash never leaves half a layout.</summary>
    public static void Write(string tempFolder, SegmentMap map)
    {
        try
        {
            Directory.CreateDirectory(tempFolder);
            string path = Path.Combine(tempFolder, FileName);
            string pending = path + ".pending";
            using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, map);
                stream.Flush(flushToDisk: true);
            }
            File.Move(pending, path, overwrite: true);
        }
        catch
        {
            // Best-effort. A map that did not land leaves the previous one, which
            // is still a correct description of every byte already written: the
            // connection that was shortened never wrote past its new end, and a
            // chunk the old map does not name is discarded rather than trusted.
        }
    }

    public static void Delete(string tempFolder)
    {
        try { File.Delete(Path.Combine(tempFolder, FileName)); } catch { /* best-effort */ }
    }
}
