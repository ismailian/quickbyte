namespace QuickByte.Core.Models;

/// <summary>
/// Application-wide, user-configurable settings. Persisted to disk as JSON
/// by <see cref="Interfaces.ISettingsService"/>.
/// </summary>
public sealed class DownloadSettings
{
    public const int MinConnections = 1;
    public const int MaxConnections = 32;
    public const int DefaultConnections = 8;

    /// <summary>Default number of simultaneous connections for new downloads (1-32).</summary>
    public int DefaultConnectionsCount { get; set; } = DefaultConnections;

    /// <summary>Maximum retry attempts per connection before it is marked Failed.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay between retries; grows with exponential backoff.</summary>
    public int RetryDelayMilliseconds { get; set; } = 1500;

    /// <summary>Folder new downloads are saved to by default.</summary>
    public string DefaultDownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "QuickByte");

    /// <summary>Folder used to store in-progress chunk (.part) files.</summary>
    public string TempFolder { get; set; } =
        Path.Combine(Path.GetTempPath(), "QuickByte");

    /// <summary>
    /// How often (ms) aggregated progress events are pushed to the UI. Kept low
    /// on purpose: the windows interpolate between samples at ~60 fps, and a
    /// short sampling interval is what makes that interpolation track reality
    /// instead of lagging visibly behind it.
    /// </summary>
    public int ProgressUpdateIntervalMilliseconds { get; set; } = 100;

    /// <summary>Buffer size (bytes) used for stream copy operations.</summary>
    public int StreamBufferSizeBytes { get; set; } = 81920; // 80 KB

    /// <summary>Maximum number of downloads actively running at once (queue throttling).</summary>
    public int MaxConcurrentDownloads { get; set; } = 3;

    /// <summary>
    /// Cap shared by every running download, in bytes per second; <c>0</c> means
    /// unlimited. Unlike the rest of this class it is honoured live — see
    /// <see cref="Services.DownloadManager"/>.
    /// </summary>
    public long GlobalSpeedLimitBytesPerSecond { get; set; }

    /// <summary>Pop the download details window open as soon as a download is added.</summary>
    public bool AutoOpenDetailsWindow { get; set; } = true;

    /// <summary>Show the "download complete" window when a download finishes.</summary>
    public bool ShowCompletionWindow { get; set; } = true;

    public int ClampConnections(int requested) =>
        Math.Clamp(requested, MinConnections, MaxConnections);
}
