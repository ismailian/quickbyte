namespace QuickByte.Core.Commands;

/// <summary>The verbs <c>qb</c> sends to a running QuickByte.</summary>
public enum CommandVerb
{
    Status = 0,
    Add = 1,
    List = 2,
    Pause = 3,
    Resume = 4,
    Queues = 5,
    QueueStart = 6,
    QueueStop = 7
}

/// <summary>
/// One command, as it crosses the pipe from <c>qb.exe</c> to QuickByte. Plain data,
/// already parsed and resolved on the command line's side — a relative folder is
/// made absolute where the working directory it is relative to still exists.
/// </summary>
public sealed class CommandRequest
{
    public CommandVerb Verb { get; set; }

    /// <summary>Add: the links.</summary>
    public List<string> Urls { get; set; } = new();

    /// <summary>Add: a folder to save into, absolute; null for the folder Options chooses.</summary>
    public string? Folder { get; set; }

    /// <summary>Add: the file name, for a single link.</summary>
    public string? FileName { get; set; }

    public int? Connections { get; set; }

    /// <summary>Add: the queue to file the downloads into, created if missing. Queue start/stop: the queue.</summary>
    public string? Queue { get; set; }

    /// <summary>Add: leave the downloads paused rather than starting them.</summary>
    public bool Paused { get; set; }

    public string? Checksum { get; set; }

    /// <summary>Add: for a streaming video, "best", "worst", or text a quality's label contains.</summary>
    public string? Quality { get; set; }

    /// <summary>Pause/resume: a download id (or the start of one), or "all".</summary>
    public string? Target { get; set; }

    /// <summary>List: active, paused, queued, completed, failed or all.</summary>
    public string? Filter { get; set; }
}

public sealed class CommandResponse
{
    public bool Ok { get; set; }

    /// <summary>What went wrong, in a sentence a person can act on.</summary>
    public string? Error { get; set; }

    public string? Message { get; set; }

    public List<DownloadSummary>? Downloads { get; set; }

    public List<QueueSummary>? Queues { get; set; }

    public StatusSummary? Status { get; set; }

    public static CommandResponse Fail(string error) => new() { Ok = false, Error = error };
}

public sealed class DownloadSummary
{
    public string Id { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Folder { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public double Percent { get; set; }
    public double BytesPerSecond { get; set; }
    public string? Queue { get; set; }
    public string? Error { get; set; }
}

public sealed class QueueSummary
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public int Files { get; set; }
    public DateTime? NextRun { get; set; }
}

public sealed class StatusSummary
{
    public string Version { get; set; } = string.Empty;
    public int Downloads { get; set; }
    public int Active { get; set; }
    public int Paused { get; set; }
    public int Queued { get; set; }
    public int Failed { get; set; }
    public int Completed { get; set; }
    public double BytesPerSecond { get; set; }
}
