using System.Text.Json;

namespace QuickByte.Core.Streaming;

/// <summary>
/// How far a stream download has got — persisted as <see cref="FileName"/> in its
/// temp folder, beside the pieces.
///
/// Resume needs no record of which pieces are done: a finished piece is a file
/// under its final name, and a piece in progress is still a <c>.part</c>. What it
/// does need is the shape those names belong to. The playlist is read again on
/// every run, and a playlist whose piece count has changed is a different cut of
/// the video — pieces numbered against the old one would be joined into a file
/// that stutters or will not play. And once every piece is down, the merge deletes
/// them as it goes, so the run after an interruption must go straight to the merge
/// rather than read a playlist that may have expired and "resume" pieces that were
/// already joined.
/// </summary>
internal sealed record StreamState
{
    public const string FileName = "stream.json";

    /// <summary>Pieces per output file, in output order: the main file, then the sound if separate.</summary>
    public List<int> PieceCounts { get; init; } = new();

    /// <summary>Every piece is on disk; what is left is the merge.</summary>
    public bool Downloaded { get; init; }

    /// <summary>How many outputs are already joined and in place.</summary>
    public int MergedTracks { get; init; }

    public static StreamState For(IReadOnlyList<StreamTrack> tracks) => new()
    {
        PieceCounts = tracks.Select(t => t.Pieces.Count).ToList()
    };

    public bool Describes(IReadOnlyList<StreamTrack> tracks) =>
        PieceCounts.SequenceEqual(tracks.Select(t => t.Pieces.Count));

    /// <summary>Whether a state read from disk is one this engine could have written.</summary>
    private bool IsPlausible =>
        PieceCounts.Count is 1 or 2
        && PieceCounts.All(c => c is > 0 and <= StreamPlan.MaxPieces + 1)
        && MergedTracks >= 0 && MergedTracks <= PieceCounts.Count
        && (Downloaded || MergedTracks == 0);

    public static StreamState? Read(string tempFolder)
    {
        try
        {
            string path = Path.Combine(tempFolder, FileName);
            if (!File.Exists(path)) return null;
            var state = JsonSerializer.Deserialize<StreamState>(File.ReadAllText(path));
            return state is { IsPlausible: true } ? state : null;
        }
        catch
        {
            return null; // unreadable reads as absent: the pieces are checked against a fresh playlist
        }
    }

    /// <summary>Written beside the state and renamed over it, so a crash never leaves half of one.</summary>
    public static void Write(string tempFolder, StreamState state)
    {
        Directory.CreateDirectory(tempFolder);
        string path = Path.Combine(tempFolder, FileName);
        string pending = path + ".pending";
        using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state);
            stream.Flush(flushToDisk: true);
        }
        File.Move(pending, path, overwrite: true);
    }

    public static void Delete(string tempFolder)
    {
        try { File.Delete(Path.Combine(tempFolder, FileName)); } catch { /* best-effort */ }
    }
}
