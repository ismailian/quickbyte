using QuickByte.Core.Enums;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.Core.Streaming;

namespace QuickByte.Core.Commands;

/// <summary>
/// Carries out a <see cref="CommandRequest"/> inside the running app, through the
/// same interfaces the windows use — a download added from a prompt is probed,
/// named, reserved, queued and started exactly as one added from Add Download, so
/// there is no second set of rules to drift.
///
/// Meant to run on the UI thread, like everything else that touches the managers
/// (the host marshals it there). It never awaits a download: adding one that starts
/// returns as soon as it is under way.
/// </summary>
public sealed class CommandProcessor
{
    private readonly IDownloadManager _downloads;
    private readonly IQueueManager _queues;
    private readonly IRemoteFileInfoProvider _probe;
    private readonly IStreamResolver? _streams;
    private readonly ISettingsService _settings;
    private readonly Func<DownloadRequest, Task<DownloadItem>> _add;
    private readonly string _version;

    /// <param name="add">
    /// How a download is added — the host's own path, so a download from the command
    /// line does not open a details window over whatever the user is doing.
    /// </param>
    public CommandProcessor(
        IDownloadManager downloads,
        IQueueManager queues,
        IRemoteFileInfoProvider probe,
        IStreamResolver? streams,
        ISettingsService settings,
        Func<DownloadRequest, Task<DownloadItem>>? add = null,
        string version = "")
    {
        _downloads = downloads;
        _queues = queues;
        _probe = probe;
        _streams = streams;
        _settings = settings;
        _add = add ?? downloads.AddDownloadAsync;
        _version = version;
    }

    public async Task<CommandResponse> ExecuteAsync(CommandRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            return request.Verb switch
            {
                CommandVerb.Add => await AddAsync(request, cancellationToken).ConfigureAwait(true),
                CommandVerb.List => List(request.Filter),
                CommandVerb.Pause => Pause(request.Target),
                CommandVerb.Resume => Resume(request.Target),
                CommandVerb.Queues => new CommandResponse { Ok = true, Queues = _queues.Queues.Select(Summarise).ToList() },
                CommandVerb.QueueStart => StartOrStopQueue(request.Queue, start: true),
                CommandVerb.QueueStop => StartOrStopQueue(request.Queue, start: false),
                _ => Status()
            };
        }
        catch (Exception ex)
        {
            return CommandResponse.Fail(ex.Message);
        }
    }

    // ------------------------------------------------------------------ add --

    private async Task<CommandResponse> AddAsync(CommandRequest request, CancellationToken cancellationToken)
    {
        if (request.Folder is { } folder && !Path.IsPathFullyQualified(folder))
            return CommandResponse.Fail($"'{folder}' is not a full folder path.");

        ExpectedChecksum? checksum = null;
        if (request.Checksum is not null && (checksum = FileChecksum.Parse(request.Checksum)) is null)
            return CommandResponse.Fail("That checksum is not one QuickByte recognises. Give the hex value, or sha256:<hex>.");

        Guid? queueId = null;
        if (!string.IsNullOrWhiteSpace(request.Queue))
            queueId = (FindQueue(request.Queue, out _) ?? _queues.Create(request.Queue.Trim())).Id;

        var added = new List<DownloadSummary>();
        var failures = new List<string>();

        foreach (string link in request.Urls)
        {
            try
            {
                var item = await AddOneAsync(link, request, checksum, cancellationToken).ConfigureAwait(true);
                if (queueId is { } id) _queues.AddToQueue(id, new[] { item.Id });
                else if (!request.Paused) _ = _downloads.StartAsync(item.Id);
                added.Add(Summarise(item));
            }
            catch (Exception ex)
            {
                failures.Add($"{link}: {Explain(ex)}");
            }
        }

        string where = queueId is null ? (request.Paused ? "paused" : "started") : $"in queue \"{_queues.Find(queueId.Value)?.Name}\"";
        return new CommandResponse
        {
            Ok = failures.Count == 0,
            Downloads = added,
            Message = added.Count == 0 ? null : $"Added {added.Count} download{(added.Count == 1 ? "" : "s")}, {where}.",
            Error = failures.Count == 0 ? null : string.Join(Environment.NewLine, failures)
        };
    }

    private async Task<DownloadItem> AddOneAsync(string link, CommandRequest request, ExpectedChecksum? checksum, CancellationToken cancellationToken)
    {
        // Lifted out of the address, as Add Download does: the address is persisted
        // and displayed, and only the credential field encrypts itself.
        var split = UrlCredentials.Extract(link);
        if (!Uri.TryCreate(split.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "ftp" or "ftps"))
            throw new FormatException("not an http, https, ftp or ftps link");

        var options = new RequestOptions { Credentials = split.Credentials };
        var info = await _probe.GetFileInfoAsync(split.Url, options, cancellationToken).ConfigureAwait(true);

        StreamSource? stream = null;
        string fileName = request.FileName ?? info.FileName;
        bool splittable = info.SupportsRangeRequests;

        if (_streams is not null && (StreamDetector.IsStream(info.FinalUrl, info.ContentType) || StreamDetector.IsStream(split.Url, null)))
        {
            var description = await _streams.DescribeAsync(split.Url, options, cancellationToken).ConfigureAwait(true);
            var choice = ChooseQuality(description, request.Quality)
                         ?? throw new FormatException($"the video offers no quality matching \"{request.Quality}\" ({string.Join(", ", description.Choices.Select(c => c.Label))})");

            stream = choice.Source;
            fileName = request.FileName is { } named
                ? Path.GetFileNameWithoutExtension(named) + choice.Extension
                : StreamDetector.SuggestFileName(split.Url, info.FileName, choice.Extension);
            info = new RemoteFileInfo { FileName = fileName, ContentType = choice.ContentType, FinalUrl = info.FinalUrl, SupportsRangeRequests = true };
            splittable = true;
        }

        var settings = _settings.Current;
        int connections = splittable ? settings.ClampConnections(request.Connections ?? settings.DefaultConnectionsCount) : 1;

        return await _add(new DownloadRequest(split.Url, info, request.Folder ?? settings.FolderFor(fileName), fileName, connections)
        {
            Credentials = split.Credentials,
            ExpectedChecksum = checksum,
            StartImmediately = false,
            Stream = stream
        }).ConfigureAwait(true);
    }

    /// <summary>"best" (the default) is the first offered, "worst" the last video, anything else the first label containing it.</summary>
    internal static StreamChoice? ChooseQuality(StreamDescription description, string? quality)
    {
        var choices = description.Choices;
        if (choices.Count == 0) return null;
        if (string.IsNullOrWhiteSpace(quality) || quality.Equals("best", StringComparison.OrdinalIgnoreCase)) return choices[0];

        if (quality.Equals("worst", StringComparison.OrdinalIgnoreCase))
            return choices.LastOrDefault(c => !c.Label.StartsWith("Audio only", StringComparison.Ordinal)) ?? choices[^1];

        return choices.FirstOrDefault(c => c.Label.Contains(quality.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string Explain(Exception ex) => ex switch
    {
        AuthenticationRequiredException =>
            "it needs a user name and password; put them in the link (https://user:password@host/...)",
        _ => ex.Message
    };

    // ---------------------------------------------------------------- state --

    private CommandResponse List(string? filter)
    {
        var items = _downloads.Downloads.Where(item => (filter ?? "all") switch
        {
            "active" => DownloadActions.IsActive(item),
            "paused" => item.Status == DownloadStatus.Paused,
            "queued" => item.Status == DownloadStatus.Queued,
            "completed" => item.Status == DownloadStatus.Completed,
            "failed" => item.Status is DownloadStatus.Failed or DownloadStatus.Cancelled,
            _ => true
        });

        return new CommandResponse { Ok = true, Downloads = items.Select(Summarise).ToList() };
    }

    private CommandResponse Status()
    {
        var items = _downloads.Downloads;
        return new CommandResponse
        {
            Ok = true,
            Status = new StatusSummary
            {
                Version = _version,
                Downloads = items.Count,
                Active = items.Count(DownloadActions.IsActive),
                Paused = items.Count(i => i.Status == DownloadStatus.Paused),
                Queued = items.Count(i => i.Status == DownloadStatus.Queued),
                Failed = items.Count(i => i.Status is DownloadStatus.Failed or DownloadStatus.Cancelled),
                Completed = items.Count(i => i.Status == DownloadStatus.Completed),
                BytesPerSecond = items.Where(i => i.Status == DownloadStatus.Downloading).Sum(i => i.CurrentSpeedBytesPerSecond)
            }
        };
    }

    private CommandResponse Pause(string? target)
    {
        if (IsAll(target))
        {
            int paused = _downloads.PauseAll();
            return new CommandResponse { Ok = true, Message = $"Paused {paused} download{(paused == 1 ? "" : "s")}." };
        }

        if (FindDownload(target, out string? problem) is not { } item) return CommandResponse.Fail(problem!);
        if (!DownloadActions.CanPause(item)) return CommandResponse.Fail($"{item.FileName} is {Describe(item.Status)}, so there is nothing to pause.");

        _downloads.Pause(item.Id);
        return new CommandResponse { Ok = true, Message = $"Paused {item.FileName}.", Downloads = new() { Summarise(item) } };
    }

    private CommandResponse Resume(string? target)
    {
        if (IsAll(target))
        {
            var resumable = _downloads.Downloads.Where(DownloadActions.CanResume).ToList();
            foreach (var item in resumable) _ = _downloads.ResumeAsync(item.Id);
            return new CommandResponse { Ok = true, Message = $"Resumed {resumable.Count} download{(resumable.Count == 1 ? "" : "s")}." };
        }

        if (FindDownload(target, out string? problem) is not { } found) return CommandResponse.Fail(problem!);
        if (!DownloadActions.CanResume(found))
            return CommandResponse.Fail($"{found.FileName} is {Describe(found.Status)}, so there is nothing to resume.");

        _ = _downloads.ResumeAsync(found.Id);
        return new CommandResponse { Ok = true, Message = $"Resumed {found.FileName}.", Downloads = new() { Summarise(found) } };
    }

    private CommandResponse StartOrStopQueue(string? name, bool start)
    {
        if (FindQueue(name, out string? problem) is not { } queue)
            return CommandResponse.Fail(problem ?? $"There is no queue called \"{name}\".");

        if (start) _queues.Start(queue.Id);
        else _queues.Stop(queue.Id);

        return new CommandResponse
        {
            Ok = true,
            Message = $"{(start ? "Started" : "Stopped")} queue \"{queue.Name}\".",
            Queues = new() { Summarise(queue) }
        };
    }

    // -------------------------------------------------------------- finding --

    private static bool IsAll(string? target) => string.Equals(target, "all", StringComparison.OrdinalIgnoreCase);

    /// <summary>By id, or the start of one — as <c>qb list</c> prints it — as long as only one download starts that way.</summary>
    private DownloadItem? FindDownload(string? target, out string? problem)
    {
        problem = null;
        string key = (target ?? string.Empty).Trim().Replace("-", string.Empty).ToLowerInvariant();
        if (key.Length < 4)
        {
            problem = "Give at least the first four characters of the download's id, as 'qb list' shows it.";
            return null;
        }

        var matches = _downloads.Downloads.Where(item => item.Id.ToString("N").StartsWith(key, StringComparison.Ordinal)).Take(2).ToList();
        if (matches.Count == 1) return matches[0];

        problem = matches.Count == 0
            ? $"No download has an id starting '{target}'."
            : $"More than one download has an id starting '{target}'; give more of it.";
        return null;
    }

    /// <summary>By name, ignoring case; or the one queue whose name starts that way.</summary>
    private DownloadQueue? FindQueue(string? name, out string? problem)
    {
        problem = null;
        string wanted = (name ?? string.Empty).Trim();
        var queues = _queues.Queues;

        var exact = queues.FirstOrDefault(q => q.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (exact is not null || wanted.Length == 0) return exact;

        var prefixed = queues.Where(q => q.Name.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        if (prefixed.Count == 1) return prefixed[0];
        if (prefixed.Count > 1) problem = $"More than one queue's name starts with \"{wanted}\".";
        return null;
    }

    // ------------------------------------------------------------- summaries --

    private DownloadSummary Summarise(DownloadItem item) => new()
    {
        Id = item.Id.ToString("N"),
        FileName = item.FileName,
        Folder = item.SaveFolder,
        Url = item.Url,
        Status = Describe(item.Status),
        TotalBytes = item.TotalBytes,
        DownloadedBytes = item.DownloadedBytes,
        Percent = Math.Round(item.ProgressPercentage, 1),
        BytesPerSecond = item.Status == DownloadStatus.Downloading ? item.CurrentSpeedBytesPerSecond : 0,
        Queue = _queues.QueueOf(item.Id)?.Name,
        Error = item.Status == DownloadStatus.Failed ? item.ErrorMessage : null
    };

    private QueueSummary Summarise(DownloadQueue queue) => new()
    {
        Id = queue.Id.ToString("N"),
        Name = queue.Name,
        State = _queues.StateOf(queue.Id).ToString().ToLowerInvariant(),
        Files = queue.ItemIds.Count,
        NextRun = _queues.NextRunAt(queue.Id)
    };

    private static string Describe(DownloadStatus status) => status switch
    {
        DownloadStatus.Cancelled => "cancelled",
        _ => status.ToString().ToLowerInvariant()
    };
}
