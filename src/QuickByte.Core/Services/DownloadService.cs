using System.Threading;
using QuickByte.Core.Enums;
using QuickByte.Core.Events;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;
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

    /// <summary>How many times one run starts over for a file that changed on the server.</summary>
    private const int MaxServerChangeRestarts = 3;

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

            // The previous run's source is finished with — a download paused and
            // resumed a dozen times would otherwise leave a dozen behind.
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            SetStatus(DownloadStatus.Connecting);

            if (string.IsNullOrWhiteSpace(Item.TempFolderPath))
                Item.TempFolderPath = Path.Combine(_settingsService.Current.TempFolder, Item.Id.ToString("N"));

            if (_lastFileInfo is null)
            {
                _lastFileInfo = await ProbeAsync(_cts.Token).ConfigureAwait(false);

                // Every fresh probe is held against the version the chunks came
                // from. For a download that never ran there are no chunks and it
                // costs nothing; for a resume after a restart, or a Retry, it is
                // the moment the server has had to replace the file.
                if (FileVersionCheck.Compare(Item.TotalBytes, Item.RemoteVersion,
                        _lastFileInfo.ContentLength, _lastFileInfo.Version) == FileVersionVerdict.Changed)
                {
                    DiscardProgress();
                }
            }

            AdoptFileInfo(_lastFileInfo);

            await RunUntilTheFileHoldsStillAsync(_cts.Token).ConfigureAwait(false);
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

    private Task<RemoteFileInfo> ProbeAsync(CancellationToken token) =>
        _fileInfoProvider.GetFileInfoAsync(Item.Url, Item.ToRequestOptions(), token);

    private void AdoptFileInfo(RemoteFileInfo info)
    {
        Item.TotalBytes = info.HasKnownSize ? info.ContentLength : Item.TotalBytes;
        Item.SupportsResume = info.SupportsRangeRequests;
        Item.ContentType = info.ContentType;

        // Taken from the fresh probe, before the pool builds its
        // connections: a Retry re-resolves the file, and if a cache in front
        // of the server is what makes ranges work, the connections have to
        // learn that from this run rather than from whatever the item was
        // added with. The probe seeds the flag from the options it was
        // handed, so an item that already knew keeps knowing.
        Item.BypassCache = info.BypassCache;

        // Whatever the server describes now is the version this run's chunks
        // belong to: either it agreed with the old one, or the old chunks are
        // already gone.
        Item.RemoteVersion = info.Version;
    }

    /// <summary>
    /// Runs the download, starting over if a connection reports that the file on
    /// the server is no longer the one the chunks came from.
    ///
    /// The report is checked before anything is thrown away. A connection's
    /// <c>If-Range</c> getting a 200 is a strong signal, but a server that does not
    /// evaluate the condition, or whose edges mint ETags of their own, sends the
    /// same answer for a file that never changed — and discarding a download on
    /// its word would cost hours for nothing. So the file is probed again: if the
    /// probe describes a different file, the chunks go; if it describes the same
    /// one, it is the condition that cannot be trusted, and the connections stop
    /// sending it.
    /// </summary>
    private async Task RunUntilTheFileHoldsStillAsync(CancellationToken token)
    {
        int restarts = 0;
        while (true)
        {
            try
            {
                await RunDownloadCycleAsync(_lastFileInfo!, token).ConfigureAwait(false);
                return;
            }
            catch (RemoteFileChangedException)
            {
                token.ThrowIfCancellationRequested();
                SetStatus(DownloadStatus.Connecting);

                var fresh = await ProbeAsync(token).ConfigureAwait(false);
                bool changed = FileVersionCheck.Compare(
                    Item.TotalBytes, Item.RemoteVersion, fresh.ContentLength, fresh.Version) == FileVersionVerdict.Changed;

                if (!changed)
                {
                    Item.IfRangeUnsupported = true;
                }
                else if (++restarts > MaxServerChangeRestarts)
                {
                    // A file regenerated on every request cannot be downloaded in
                    // pieces at all; saying so beats restarting forever.
                    throw new IOException("The file on the server kept changing while it was being downloaded.");
                }
                else
                {
                    DiscardProgress();
                }

                _lastFileInfo = fresh;
                AdoptFileInfo(fresh);
            }
        }
    }

    /// <summary>
    /// Throws away every chunk, so the next run starts the file from zero. Used
    /// only once the server is known to hold a different version: bytes from two
    /// versions of a file merge into a file that is neither.
    /// </summary>
    private void DiscardProgress()
    {
        _poolManager.DiscardChunks(Item.TempFolderPath);
        Item.DownloadedBytes = 0;
        Item.MergeProgressPercentage = 0;
    }

    private async Task RunDownloadCycleAsync(RemoteFileInfo fileInfo, CancellationToken token)
    {
        // A pause that landed while the probe was answering has already set the
        // status; announcing Downloading over it would un-pause the row.
        token.ThrowIfCancellationRequested();
        SetStatus(DownloadStatus.Downloading);

        bool completed = await _poolManager
            .RunAsync(Item, fileInfo, _settingsService.Current, token)
            .ConfigureAwait(false);

        token.ThrowIfCancellationRequested();

        if (!completed)
        {
            // Prefer what actually went wrong. OnPoolConnectionFailed records the
            // first connection's own message, and SetStatus(Connecting) cleared
            // the field at the top of this run, so anything in it belongs to this
            // attempt. "404 (Not Found)" is a message a user can act on; the
            // generic line below is one they can only report.
            SetStatus(DownloadStatus.Failed,
                Item.ErrorMessage ?? "One or more connections failed after exhausting retries.");
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

        // Deliberately not Progress<T>. That posts to the captured
        // SynchronizationContext and silently falls back to the thread pool when
        // there is none — and this runs on a pool thread, where there is none. The
        // reports then arrive out of order and the merge percentage walks
        // backwards. Nothing here needs marshaling anyway: DownloadManager wraps
        // every re-published event in _dispatcher.Post, exactly as it does for the
        // pool's own progress timer.
        var mergeProgress = new InlineProgress(merged =>
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

        await _fileMerger.MergeAsync(chunkPaths, destination, _settingsService.Current.ClampBufferSize(), mergeProgress, token)
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

        // A finished download has nothing to cancel and no chunks left to
        // discard. Rewriting it as Cancelled is not harmless: DownloadManager
        // calls Stop() on its way through Remove(), so removing a completed
        // download used to persist it as cancelled and announce that status to
        // every open window a moment before the row disappeared.
        if (Item.Status == DownloadStatus.Completed) return;

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

    /// <summary>
    /// An <see cref="IProgress{T}"/> that runs its callback on the thread that
    /// reported, in the order the reports were made. See the note at its one use
    /// site for why <see cref="Progress{T}"/> is the wrong tool here.
    /// </summary>
    private sealed class InlineProgress : IProgress<long>
    {
        private readonly Action<long> _report;

        public InlineProgress(Action<long> report) => _report = report;

        public void Report(long value) => _report(value);
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
