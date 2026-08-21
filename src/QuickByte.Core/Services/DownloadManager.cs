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

    public async Task<DownloadItem> AddDownloadAsync(string url, RemoteFileInfo fileInfo, string saveFolder, string fileName, int connectionsCount)
    {
        Directory.CreateDirectory(saveFolder);
        string safeName = FileNameHelper.SanitizeFileName(fileName);
        string finalPath = FileNameHelper.GetAvailableFilePath(saveFolder, safeName);

        var item = new DownloadItem
        {
            Url = url,
            FileName = Path.GetFileName(finalPath),
            SaveFolder = saveFolder,
            ContentType = fileInfo.ContentType,
            TotalBytes = fileInfo.ContentLength,
            ConnectionsCount = _settingsService.Current.ClampConnections(connectionsCount),
            SupportsResume = fileInfo.SupportsRangeRequests,
            Status = DownloadStatus.Queued,
            TempFolderPath = Path.Combine(_settingsService.Current.TempFolder, Guid.NewGuid().ToString("N"))
        };

        _items[item.Id] = item;
        RegisterService(item);
        PersistAll();

        DownloadListChanged?.Invoke(this, new DownloadListChangedEventArgs { ChangeType = DownloadListChangeType.Added, Item = item });

        await StartAsync(item.Id).ConfigureAwait(false);
        return item;
    }

    private IDownloadService RegisterService(DownloadItem item)
    {
        var speedLimit = new BandwidthLimiter(item.SpeedLimitBytesPerSecond);
        _speedLimits[item.Id] = speedLimit;

        var poolManager = new ConnectionPoolManager(
            _connectionFactory, new CompositeBandwidthLimiter(speedLimit, _globalSpeedLimit));
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
