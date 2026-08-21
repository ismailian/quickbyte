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
