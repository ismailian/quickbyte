using QuickByte.Core.Events;
using QuickByte.Core.Models;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// Facade / registry the UI layer talks to. Owns the collection of
/// <see cref="IDownloadService"/> instances, persists them via the
/// repository, and re-publishes their events under one roof so both the
/// main window and any number of open detail windows stay in sync.
/// This is the single composition point that makes multi-window
/// synchronization possible without windows knowing about each other.
/// </summary>
public interface IDownloadManager
{
    IReadOnlyList<DownloadItem> Downloads { get; }

    event EventHandler<DownloadListChangedEventArgs>? DownloadListChanged;
    event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;
    event EventHandler<ConnectionsSnapshotEventArgs>? ConnectionsChanged;

    Task<DownloadItem> AddDownloadAsync(DownloadRequest request);

    IDownloadService? GetService(Guid downloadId);

    Task StartAsync(Guid downloadId);
    void Pause(Guid downloadId);

    /// <summary>
    /// Pauses every download currently in flight, leaving their chunk files in
    /// place so each one resumes where it stopped. Returns the number actually
    /// paused. Used both by "Stop All" and by application shutdown — the manager
    /// owns this rather than the UI because it is the only component that can
    /// see every service, and because each pause persists through the same
    /// repository write.
    /// </summary>
    int PauseAll();

    Task ResumeAsync(Guid downloadId);
    void Stop(Guid downloadId);
    Task RetryAsync(Guid downloadId);
    void Remove(Guid downloadId, bool deleteFile);

    /// <summary>The application-wide cap in bytes per second; 0 means unlimited.</summary>
    long GlobalSpeedLimitBytesPerSecond { get; }

    /// <summary>
    /// Caps one download at <paramref name="bytesPerSecond"/> (0 lifts the cap)
    /// and persists it on the item. Applies to a transfer already in flight —
    /// the limiter is a live object rather than a snapshotted setting.
    /// </summary>
    void SetSpeedLimit(Guid downloadId, long bytesPerSecond);

    /// <summary>
    /// Caps every running download to a shared <paramref name="bytesPerSecond"/>
    /// budget (0 lifts the cap). Also applies mid-transfer. Persisting the value
    /// is the settings layer's job; this only moves the live limiter.
    /// </summary>
    void SetGlobalSpeedLimit(long bytesPerSecond);

    void LoadPersistedDownloads();

    /// <summary>
    /// Deletes chunk folders under the temp root that no download in the list
    /// claims, and returns how many went. A stopped download whose connections
    /// were still holding their part files open leaves its folder behind, and
    /// nothing else ever revisits it — so the app would quietly accumulate the
    /// full size of every cancelled download. Runs off the UI thread and after
    /// <see cref="LoadPersistedDownloads"/>, which is what makes "not claimed"
    /// mean "orphaned" rather than "not loaded yet".
    /// </summary>
    Task<int> CleanupOrphanedTempFoldersAsync();
}
