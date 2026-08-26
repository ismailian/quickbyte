using System.Threading;
using System.Collections.Concurrent;
using QuickByte.Core.Enums;
using QuickByte.Core.Events;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// Composition-root facade: owns every <see cref="IDownloadService"/>
/// instance, persists the download list, throttles how many downloads run
/// concurrently (queueing the rest), and re-publishes each service's events
/// under one set of manager-level events. Every window (main + any number
/// of detail windows) subscribes only to this manager — never to each
/// other — which is what keeps them synchronized with no duplicate polling
/// and no risk of missed updates between windows.
/// </summary>
public sealed class DownloadManager : IDownloadManager
{
    private readonly IDownloadRepository _repository;
    private readonly ISettingsService _settingsService;
    private readonly IRemoteFileInfoProvider _fileInfoProvider;
    private readonly IConnectionFactory _connectionFactory;
    private readonly IFileMerger _fileMerger;
    private readonly IDispatcher _dispatcher;

    private readonly ConcurrentDictionary<Guid, IDownloadService> _services = new();
    private readonly ConcurrentDictionary<Guid, DownloadItem> _items = new();
    private readonly SemaphoreSlim _concurrencyGate;

    // Bandwidth throttling sits here for the same reason the concurrency gate
    // does: it is a budget shared across downloads, and the manager is the only
    // component that can see all of them. One limiter per download plus one for
    // the whole app; each pool gets a composite of its own and the global one.
    private readonly ConcurrentDictionary<Guid, BandwidthLimiter> _speedLimits = new();

    // The queue tier. A separate limiter rather than a value folded into the one
    // above: a download's own limit is the user's and is persisted, this one
    // belongs to whichever queue is running it and has to be liftable without
    // touching what the user chose.
    private readonly ConcurrentDictionary<Guid, BandwidthLimiter> _queueSpeedLimits = new();

    private readonly BandwidthLimiter _globalSpeedLimit;

    public event EventHandler<DownloadListChangedEventArgs>? DownloadListChanged;
    public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    public event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<ConnectionsSnapshotEventArgs>? ConnectionsChanged;

    public IReadOnlyList<DownloadItem> Downloads => _items.Values.OrderByDescending(i => i.CreatedAt).ToList();

    public DownloadManager(
        IDownloadRepository repository,
        ISettingsService settingsService,
        IRemoteFileInfoProvider fileInfoProvider,
        IConnectionFactory connectionFactory,
        IFileMerger fileMerger,
        IDispatcher dispatcher)
    {
        _repository = repository;
        _settingsService = settingsService;
        _fileInfoProvider = fileInfoProvider;
        _connectionFactory = connectionFactory;
        _fileMerger = fileMerger;
        _dispatcher = dispatcher;
        _concurrencyGate = new SemaphoreSlim(Math.Max(1, settingsService.Current.MaxConcurrentDownloads));
        _globalSpeedLimit = new BandwidthLimiter(settingsService.Current.GlobalSpeedLimitBytesPerSecond);

        // The one setting that is honoured live rather than snapshotted. A speed
        // limit you have to restart the app to apply is not a speed limit, and
        // the limiter is a mutable object, so keeping it in step costs a single
        // assignment. Everything else in DownloadSettings still takes effect on
        // the next download, by design.
        _settingsService.SettingsChanged += (_, settings) =>
            _globalSpeedLimit.BytesPerSecond = settings.GlobalSpeedLimitBytesPerSecond;
    }

    public long GlobalSpeedLimitBytesPerSecond => _globalSpeedLimit.BytesPerSecond;

    public void SetGlobalSpeedLimit(long bytesPerSecond) =>
        _globalSpeedLimit.BytesPerSecond = bytesPerSecond;

    public void SetQueueSpeedLimit(Guid downloadId, long bytesPerSecond)
    {
        if (_queueSpeedLimits.TryGetValue(downloadId, out var limiter))
            limiter.BytesPerSecond = Math.Max(0, bytesPerSecond);
    }

    public void SetSpeedLimit(Guid downloadId, long bytesPerSecond)
    {
        bytesPerSecond = Math.Max(0, bytesPerSecond);

        if (_speedLimits.TryGetValue(downloadId, out var limiter))
            limiter.BytesPerSecond = bytesPerSecond;

        if (!_items.TryGetValue(downloadId, out var item)) return;
        item.SpeedLimitBytesPerSecond = bytesPerSecond;
        PersistAll();
    }

    public void LoadPersistedDownloads()
    {
        foreach (var item in _repository.LoadAll())
        {
            // Anything that was mid-flight when the app last closed resumes as Paused.
            if (item.Status is DownloadStatus.Downloading or DownloadStatus.Connecting or DownloadStatus.Merging)
                item.Status = DownloadStatus.Paused;

            _items[item.Id] = item;
            RegisterService(item);
        }
    }

    public Task<int> CleanupOrphanedTempFoldersAsync() => Task.Run(() =>
    {
        string root = _settingsService.Current.TempFolder;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return 0;

        string[] candidates;
        try { candidates = Directory.GetDirectories(root); }
        catch { return 0; /* best-effort */ }

        int deleted = 0;
        foreach (string candidate in candidates)
        {
            // Only folders this app names. TempFolder is user-configurable and
            // may well point at a directory shared with other programs, so the
            // sweep is deliberately restricted to the Guid("N") shape both
            // AddDownloadAsync and DownloadService create — anything else in
            // there belongs to someone else.
            if (!Guid.TryParseExact(Path.GetFileName(candidate), "N", out _)) continue;

            // Re-checked per folder rather than against one up-front snapshot:
            // a download added while the sweep runs must not lose its chunks.
            if (IsTempFolderInUse(candidate)) continue;

            try
            {
                Directory.Delete(candidate, recursive: true);
                deleted++;
            }
            catch { /* best-effort — locked or already gone */ }
        }
        return deleted;
    });

    private bool IsTempFolderInUse(string path)
    {
        string normalized = NormalizePath(path);
        foreach (var item in _items.Values)
        {
            if (string.IsNullOrEmpty(item.TempFolderPath)) continue;

            // A cancelled download keeps its TempFolderPath on the item but has
            // already disowned the chunks — Stop() means discard, unlike Pause().
            // Its folder only still exists because the delete lost the race with
            // the connections closing, so it is exactly what this sweep is for.
            if (item.Status == DownloadStatus.Cancelled) continue;

            if (string.Equals(NormalizePath(item.TempFolderPath), normalized, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string NormalizePath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
        catch { return path; }
    }

    public async Task<DownloadItem> AddDownloadAsync(DownloadRequest request)
    {
        Directory.CreateDirectory(request.SaveFolder);
        string safeName = FileNameHelper.SanitizeFileName(request.FileName);
        string finalPath = FileNameHelper.GetAvailableFilePath(request.SaveFolder, safeName);

        var item = new DownloadItem
        {
            Url = request.Url,
            FileName = Path.GetFileName(finalPath),
            SaveFolder = request.SaveFolder,
            ContentType = request.FileInfo.ContentType,
            TotalBytes = request.FileInfo.ContentLength,
            ConnectionsCount = _settingsService.Current.ClampConnections(request.ConnectionsCount),
            SupportsResume = request.FileInfo.SupportsRangeRequests,
            Status = DownloadStatus.Queued,
            TempFolderPath = Path.Combine(_settingsService.Current.TempFolder, Guid.NewGuid().ToString("N")),
            Credentials = request.Credentials?.Clone(),
            Headers = request.Headers is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase)
        };

        _items[item.Id] = item;
        RegisterService(item);
        PersistAll();

        DownloadListChanged?.Invoke(this, new DownloadListChangedEventArgs { ChangeType = DownloadListChangeType.Added, Item = item });

        // A download destined for a queue is added and left alone: the queue
        // decides when it runs, and starting it here would be the one thing the
        // user asked not to happen by choosing a queue at all.
        if (request.StartImmediately)
            await StartAsync(item.Id).ConfigureAwait(false);

        return item;
    }

    private IDownloadService RegisterService(DownloadItem item)
    {
        var speedLimit = new BandwidthLimiter(item.SpeedLimitBytesPerSecond);
        _speedLimits[item.Id] = speedLimit;

        // Starts unlimited and stays that way unless a queue claims the download:
        // an unset limiter short-circuits before taking a lock, so the tier costs
        // nothing for the downloads that are in no queue.
        var queueSpeedLimit = new BandwidthLimiter();
        _queueSpeedLimits[item.Id] = queueSpeedLimit;

        var poolManager = new ConnectionPoolManager(
            _connectionFactory, new CompositeBandwidthLimiter(speedLimit, queueSpeedLimit, _globalSpeedLimit));
        var service = new DownloadService(item, poolManager, _fileMerger, _fileInfoProvider, _settingsService, PersistItem);

        service.ProgressChanged += (_, e) => _dispatcher.Post(() => ProgressChanged?.Invoke(this, e));
        service.StatusChanged += (_, e) => _dispatcher.Post(() => StatusChanged?.Invoke(this, e));
        service.ConnectionsChanged += (_, e) => _dispatcher.Post(() => ConnectionsChanged?.Invoke(this, e));

        _services[item.Id] = service;
        return service;
    }

    public IDownloadService? GetService(Guid downloadId) => _services.GetValueOrDefault(downloadId);

    public async Task StartAsync(Guid downloadId)
    {
        if (!_services.TryGetValue(downloadId, out var service)) return;

        await _concurrencyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await service.StartAsync().ConfigureAwait(false);
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    public void Pause(Guid downloadId) => _services.GetValueOrDefault(downloadId)?.Pause();

    public int PauseAll()
    {
        int paused = 0;
        foreach (var service in _services.Values)
        {
            // Ask the item, not the service: Pause() is a no-op for anything that
            // isn't in flight, and the count is what lets a caller (shutdown) know
            // whether it has anything to wait on.
            if (!DownloadActions.CanPause(service.Item)) continue;
            service.Pause();
            paused++;
        }
        return paused;
    }

    public async Task ResumeAsync(Guid downloadId) => await StartAsync(downloadId).ConfigureAwait(false);

    public void Stop(Guid downloadId) => _services.GetValueOrDefault(downloadId)?.Stop();

    public async Task RetryAsync(Guid downloadId)
    {
        if (!_services.TryGetValue(downloadId, out var service)) return;
        await _concurrencyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await service.RetryAsync().ConfigureAwait(false);
        }
        finally
        {
            _concurrencyGate.Release();
        }
    }

    public void Remove(Guid downloadId, bool deleteFile)
    {
        if (_services.TryRemove(downloadId, out var service))
        {
            service.Stop();
            service.Dispose();
        }

        _speedLimits.TryRemove(downloadId, out _);
        _queueSpeedLimits.TryRemove(downloadId, out _);

        if (_items.TryRemove(downloadId, out var item))
        {
            if (deleteFile)
            {
                try { if (File.Exists(item.FullPath)) File.Delete(item.FullPath); } catch { /* best-effort */ }
            }
            PersistAll();
            DownloadListChanged?.Invoke(this, new DownloadListChangedEventArgs { ChangeType = DownloadListChangeType.Removed, Item = item });
        }
    }

    private void PersistItem(DownloadItem item)
    {
        _items[item.Id] = item;
        PersistAll();
    }

    private void PersistAll() => _repository.SaveAll(_items.Values);
}
