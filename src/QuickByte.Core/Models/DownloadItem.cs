using QuickByte.Core.Enums;

namespace QuickByte.Core.Models;

/// <summary>
/// Plain data record describing a download. This is the object persisted to
/// disk (downloads.json) and displayed in the main window ListView. Runtime
/// orchestration (connections, threads, HttpClient) lives in
/// <see cref="Services.DownloadService"/> — kept separate so the model stays
/// a cheap, serializable POCO with no threading concerns.
/// </summary>
public sealed class DownloadItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Url { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string SaveFolder { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;

    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }

    public int ConnectionsCount { get; set; } = DownloadSettings.DefaultConnections;
    public bool SupportsResume { get; set; }

    /// <summary>
    /// Login for the server this file comes from, or null for anonymous access.
    /// Persisted with the password encrypted — see <see cref="DownloadCredentials"/>.
    /// It has to live on the item rather than only on the request that created
    /// it: a paused download resumed three days later has to present the same
    /// login, and so does every retry.
    /// </summary>
    public DownloadCredentials? Credentials { get; set; }

    /// <summary>
    /// Extra request headers, currently only ever populated by the browser
    /// extension (cookie, referrer, user agent). Same reasoning as
    /// <see cref="Credentials"/>: a session cookie that resolved the link at
    /// capture time is what lets the resume three minutes later fetch the same
    /// bytes rather than a login page.
    /// </summary>
    /// <remarks>
    /// The setter coalesces because this is deserialized state. A property
    /// initializer only survives a member that is *absent* from the JSON — an
    /// explicit <c>"Headers": null</c> overwrites it, and every read here is on
    /// the path a persisted download takes when it resumes. The rest of the load
    /// path already prefers an empty default to failing startup; this keeps that
    /// promise for one more field.
    /// </remarks>
    public Dictionary<string, string> Headers
    {
        get => _headers;
        set => _headers = value ?? new Dictionary<string, string>();
    }

    private Dictionary<string, string> _headers = new();

    /// <summary>
    /// Cap for this download alone, in bytes per second; <c>0</c> means
    /// unlimited. Persisted, so a limit set on a big download survives a pause
    /// or an app restart rather than quietly reverting to full speed.
    /// </summary>
    public long SpeedLimitBytesPerSecond { get; set; }

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;
    public string? ErrorMessage { get; set; }

    public double CurrentSpeedBytesPerSecond { get; set; }
    public TimeSpan? EstimatedTimeRemaining { get; set; }

    /// <summary>
    /// 0-100 while <see cref="DownloadStatus.Merging"/>. Kept separate from
    /// <see cref="DownloadedBytes"/> so merging never rewinds the progress bar.
    /// </summary>
    public double MergeProgressPercentage { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? CompletedAt { get; set; }

    public string FullPath => Path.Combine(SaveFolder, FileName);
    public string TempFolderPath { get; set; } = string.Empty;

    /// <summary>Bundles the two fields above into the shape the engine takes.</summary>
    public RequestOptions ToRequestOptions() => new()
    {
        Credentials = Credentials,
        Headers = Headers.Count > 0 ? Headers : null
    };

    public double ProgressPercentage =>
        TotalBytes <= 0 ? 0 : Math.Min(100.0, (double)DownloadedBytes / TotalBytes * 100.0);

    public DownloadCategory Category => Status switch
    {
        DownloadStatus.Queued => DownloadCategory.Queued,
        DownloadStatus.Connecting or DownloadStatus.Downloading or DownloadStatus.Merging => DownloadCategory.InProgress,
        DownloadStatus.Paused => DownloadCategory.Paused,
        DownloadStatus.Completed => DownloadCategory.Completed,
        DownloadStatus.Failed or DownloadStatus.Cancelled => DownloadCategory.Failed,
        _ => DownloadCategory.All
    };
}
