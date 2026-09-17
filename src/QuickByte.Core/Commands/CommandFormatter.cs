using System.Globalization;
using System.Text;
using System.Text.Json;
using QuickByte.Core.Helpers;

namespace QuickByte.Core.Commands;

/// <summary>
/// Turns a response into what <c>qb</c> prints: aligned columns for a person, or the
/// response itself as JSON for a script (<c>--json</c>). Pure, and separate from the
/// console so the shape of the output is tested — a script that parses it depends
/// on that shape. The columns use ASCII punctuation: a console on an old code page
/// turns a dash into a question mark.
/// </summary>
public static class CommandFormatter
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>The short id <c>qb list</c> shows, and <c>qb pause</c> accepts.</summary>
    public const int ShortIdLength = 8;

    public static string Format(CommandRequest request, CommandResponse response, bool json)
    {
        if (json) return JsonSerializer.Serialize(response, Json);

        var text = new StringBuilder();
        if (response.Message is { } message) text.AppendLine(message);

        if (response.Status is { } status)
        {
            text.AppendLine($"QuickByte {status.Version}".TrimEnd());
            text.AppendLine($"{status.Downloads} downloads: {status.Active} active, {status.Queued} queued, {status.Paused} paused, {status.Failed} failed, {status.Completed} complete");
            if (status.Active > 0) text.AppendLine($"Speed: {ByteFormatter.FormatSpeed(status.BytesPerSecond)}");
        }

        if (response.Downloads is { Count: > 0 } downloads && (request.Verb == CommandVerb.List || request.Verb == CommandVerb.Add))
            AppendDownloads(text, downloads);
        else if (request.Verb == CommandVerb.List && response.Ok)
            text.AppendLine("No downloads.");

        if (response.Queues is { Count: > 0 } queues && request.Verb == CommandVerb.Queues)
            AppendQueues(text, queues);
        else if (request.Verb == CommandVerb.Queues && response.Ok)
            text.AppendLine("No queues.");

        return text.ToString().TrimEnd();
    }

    private static void AppendDownloads(StringBuilder text, List<DownloadSummary> downloads)
    {
        var rows = downloads.Select(d => new[]
        {
            d.Id.Length > ShortIdLength ? d.Id[..ShortIdLength] : d.Id,
            d.Status,
            d.TotalBytes > 0 ? d.Percent.ToString("0.0", CultureInfo.CurrentCulture) + "%" : "-",
            d.TotalBytes > 0 ? ByteFormatter.FormatBytes(d.TotalBytes) : "-",
            d.BytesPerSecond > 0 ? ByteFormatter.FormatSpeed(d.BytesPerSecond) : string.Empty,
            d.FileName + (d.Queue is { } queue ? $"  [{queue}]" : string.Empty) + (d.Error is { } error ? $"  - {error}" : string.Empty)
        }).ToList();

        AppendTable(text, new[] { "ID", "STATUS", "DONE", "SIZE", "SPEED", "NAME" }, rows);
    }

    private static void AppendQueues(StringBuilder text, List<QueueSummary> queues)
    {
        var rows = queues.Select(q => new[]
        {
            q.Name,
            q.State,
            q.Files.ToString(CultureInfo.CurrentCulture),
            q.NextRun is { } next ? next.ToString("ddd d MMM HH:mm", CultureInfo.CurrentCulture) : "-"
        }).ToList();

        AppendTable(text, new[] { "QUEUE", "STATE", "FILES", "NEXT RUN" }, rows);
    }

    private static void AppendTable(StringBuilder text, string[] header, List<string[]> rows)
    {
        int[] widths = header.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();

        void Row(string[] cells)
        {
            // The last column is not padded: it is the one that may run long.
            string line = string.Join("  ", cells.Select((cell, i) => i == cells.Length - 1 ? cell : cell.PadRight(widths[i])));
            text.AppendLine(line.TrimEnd());
        }

        Row(header);
        foreach (var row in rows) Row(row);
    }
}
