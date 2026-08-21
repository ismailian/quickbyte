using System.Threading;
using QuickByte.Core.Enums;
using QuickByte.Core.Events;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// Orchestrates one download's full lifecycle: Queued -> Connecting ->
/// Downloading -> Merging -> Completed (or Paused/Failed/Cancelled along
/// the way). Delegates the actual byte-fetching to
/// <see cref="IConnectionPoolManager"/> and the final assembly to
/// <see cref="IFileMerger"/>, and re-publishes their progress under a
/// stable set of events that both the main window and any open download
/// details window subscribe to directly — that shared source of truth is
/// what keeps every open window in sync.
/// </summary>
public sealed class DownloadService : IDownloadService
{
    private readonly IConnectionPoolManager _poolManager;
    private readonly IFileMerger _fileMerger;
    private readonly IRemoteFileInfoProvider _fileInfoProvider;
    private readonly ISettingsService _settingsService;
    private readonly Action<DownloadItem> _persist;

    private const int TempDeleteAttempts = 10;
    private const int TempDeleteRetryDelayMilliseconds = 250;

    private CancellationTokenSource? _cts;
    private RemoteFileInfo? _lastFileInfo;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);

    public DownloadItem Item { get; }

    public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    public event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<ConnectionsSnapshotEventArgs>? ConnectionsChanged;

    public DownloadService(
        DownloadItem item,
        IConnectionPoolManager poolManager,
        IFileMerger fileMerger,
        IRemoteFileInfoProvider fileInfoProvider,
        ISettingsService settingsService,
        Action<DownloadItem> persist)
    {
        Item = item;
        _poolManager = poolManager;
        _fileMerger = fileMerger;
        _fileInfoProvider = fileInfoProvider;
        _settingsService = settingsService;
        _persist = persist;

        _poolManager.ProgressChanged += OnPoolProgressChanged;
        _poolManager.ConnectionsChanged += OnPoolConnectionsChanged;
        _poolManager.ConnectionFailed += OnPoolConnectionFailed;
    }

    public async Task StartAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Item.Status is DownloadStatus.Downloading or DownloadStatus.Connecting) return;

            _cts = new CancellationTokenSource();
            SetStatus(DownloadStatus.Connecting);

            _lastFileInfo ??= await _fileInfoProvider.GetFileInfoAsync(Item.Url, _cts.Token).ConfigureAwait(false);
            Item.TotalBytes = _lastFileInfo.HasKnownSize ? _lastFileInfo.ContentLength : Item.TotalBytes;
            Item.SupportsResume = _lastFileInfo.SupportsRangeRequests;
            Item.ContentType = _lastFileInfo.ContentType;
            if (string.IsNullOrWhiteSpace(Item.TempFolderPath))
                Item.TempFolderPath = Path.Combine(_settingsService.Current.TempFolder, Item.Id.ToString("N"));

            await RunDownloadCycleAsync(_lastFileInfo, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Pause()/Stop() already set the appropriate status.
        }
        catch (Exception ex)
        {
            SetStatus(DownloadStatus.Failed, ex.Message);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task RunDownloadCycleAsync(RemoteFileInfo fileInfo, CancellationToken token)
    {
        SetStatus(DownloadStatus.Downloading);

        bool completed = await _poolManager
            .RunAsync(Item, fileInfo, _settingsService.Current, token)
            .ConfigureAwait(false);

        token.ThrowIfCancellationRequested();

        if (!completed)
        {
            SetStatus(DownloadStatus.Failed, "One or more connections failed after exhausting retries.");
            return;
        }

        // Every byte is already on disk before the merge starts, so the overall
        // progress is pinned at 100% for the whole merge. Merge progress travels
        // on its own channel (MergePercentage) — reporting it as DownloadedBytes
        // is what used to make the bar visibly rewind and climb again.
        if (Item.TotalBytes > 0) Item.DownloadedBytes = Item.TotalBytes;
        Item.MergeProgressPercentage = 0;
        SetStatus(DownloadStatus.Merging);

        string destination = Item.FullPath;
        var chunkPaths = _poolManager.GetOrderedChunkPaths();
        var mergeProgress = new Progress<long>(merged =>
        {
            double percentage = Item.TotalBytes > 0
                ? Math.Clamp(merged * 100.0 / Item.TotalBytes, 0, 100)
                : 0;
            Item.MergeProgressPercentage = percentage;
            ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
            {
                DownloadId = Item.Id,
                DownloadedBytes = Item.DownloadedBytes,
                TotalBytes = Item.TotalBytes,
                SpeedBytesPerSecond = 0,
                EstimatedTimeRemaining = TimeSpan.Zero,
                MergePercentage = percentage
            });
        });

        await _fileMerger.MergeAsync(chunkPaths, destination, _settingsService.Current.StreamBufferSizeBytes, mergeProgress, token)
            .ConfigureAwait(false);

        _fileMerger.CleanupChunks(chunkPaths, Item.TempFolderPath);

        Item.MergeProgressPercentage = 100;
        Item.DownloadedBytes = Item.TotalBytes;
        Item.CompletedAt = DateTimeOffset.Now;
        SetStatus(DownloadStatus.Completed);
    }

    public void Pause()
    {
        if (Item.Status is not (DownloadStatus.Downloading or DownloadStatus.Connecting)) return;
        SetStatus(DownloadStatus.Paused);
        _cts?.Cancel();
    }

    public async Task ResumeAsync()
    {
        if (Item.Status is DownloadStatus.Downloading or DownloadStatus.Connecting) return;
        await StartAsync().ConfigureAwait(false);
    }

    public void Stop()
    {
        _cts?.Cancel();
        SetStatus(DownloadStatus.Cancelled);
        ScheduleTempFolderDeletion();
    }

    public async Task RetryAsync()
    {
        Item.ErrorMessage = null;
        _lastFileInfo = null; // re-resolve in case the server-side file changed
        await StartAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the download's chunk folder, retrying for a couple of seconds.
    /// <see cref="Stop"/> cancels and returns immediately, but the connection
    /// tasks are still unwinding at that point and still hold their part files
    /// open, so a delete issued inline loses the race every time — which is how
    /// a stopped download used to leave its chunks occupying disk. Bails out if
    /// the download comes back to life in the meantime (Retry), so a fresh set
    /// of chunks can't be deleted out from under it.
    /// </summary>
    private void ScheduleTempFolderDeletion()
    {
        string path = Item.TempFolderPath;
        if (string.IsNullOrEmpty(path)) return;

        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < TempDeleteAttempts; attempt++)
            {
                if (Item.Status is not DownloadStatus.Cancelled) return;

                try
                {
                    if (!Directory.Exists(path)) return;
                    Directory.Delete(path, recursive: true);
                    return;
                }
                catch { /* best-effort — a connection still has a chunk open */ }

                await Task.Delay(TempDeleteRetryDelayMilliseconds).ConfigureAwait(false);
            }
            // Still locked after all that: DownloadManager's startup sweep
            // collects it, which is why that sweep treats a cancelled item's
            // TempFolderPath as unclaimed rather than in use.
        });
    }

    private void OnPoolProgressChanged(object? sender, DownloadProgressEventArgs e)
    {
        Item.DownloadedBytes = e.DownloadedBytes;
        Item.TotalBytes = e.TotalBytes > 0 ? e.TotalBytes : Item.TotalBytes;
        Item.CurrentSpeedBytesPerSecond = e.SpeedBytesPerSecond;
        Item.EstimatedTimeRemaining = e.EstimatedTimeRemaining;
        ProgressChanged?.Invoke(this, e);
    }

    private void OnPoolConnectionsChanged(object? sender, ConnectionsSnapshotEventArgs e) =>
        ConnectionsChanged?.Invoke(this, e);

    private void OnPoolConnectionFailed(object? sender, string message) =>
        Item.ErrorMessage = message;

    private void SetStatus(DownloadStatus newStatus, string? errorMessage = null)
    {
        var old = Item.Status;
        Item.Status = newStatus;
        Item.ErrorMessage = errorMessage;
        _persist(Item);
        StatusChanged?.Invoke(this, new DownloadStatusChangedEventArgs
        {
            DownloadId = Item.Id,
            OldStatus = old,
            NewStatus = newStatus,
            ErrorMessage = errorMessage
        });
    }

    public void Dispose()
    {
        _poolManager.ProgressChanged -= OnPoolProgressChanged;
        _poolManager.ConnectionsChanged -= OnPoolConnectionsChanged;
        _poolManager.ConnectionFailed -= OnPoolConnectionFailed;
        _cts?.Cancel();
        _cts?.Dispose();
        _lifecycleLock.Dispose();
    }
}
