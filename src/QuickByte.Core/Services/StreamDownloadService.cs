using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using QuickByte.Core.Enums;
using QuickByte.Core.Events;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.Core.Streaming;

namespace QuickByte.Core.Services;

/// <summary>
/// One streaming video's lifecycle — the <see cref="DownloadService"/> of a
/// download whose <see cref="DownloadItem.Stream"/> is set. Same statuses, same
/// events, same pause/resume/stop semantics; only where the bytes come from
/// differs.
///
/// A stream is not one file split into ranges but hundreds of small files listed
/// by a playlist. So instead of a connection pool over byte ranges this reads the
/// playlist, fetches up to <see cref="DownloadItem.ConnectionsCount"/> pieces at
/// once, and joins them with the same resumable <see cref="IFileMerger"/> — one
/// output per track, since the sound is often a playlist of its own and there is
/// no muxer here to put it back inside the video.
/// </summary>
/// <remarks>
/// <para><b>Resume.</b> A finished piece is renamed from <c>.part</c> to its final
/// name, so a piece's presence is the whole record of it; a <c>.part</c> is carried
/// on from its length, like a chunk. Except when it is encrypted: AES-CBC cannot be
/// continued from plaintext, so an encrypted piece starts again. Pieces are small
/// and that costs seconds. <see cref="StreamState"/> holds the rest.</para>
///
/// <para><b>Decryption.</b> AES-128 (HLS's whole-piece encryption) is decrypted as
/// the piece is written, never stored encrypted. The key is fetched once per
/// address with the download's cookies — a player's session is often what the key
/// server checks — and held only in memory. DRM schemes are refused when the
/// playlist is read (<see cref="StreamPlan"/>).</para>
/// </remarks>
public sealed class StreamDownloadService : IDownloadService
{
    private const int TempDeleteAttempts = 10;
    private const int TempDeleteRetryDelayMilliseconds = 250;
    private const int AesKeyBytes = 16;

    private readonly HttpClient _client;
    private readonly IFileMerger _fileMerger;
    private readonly ISettingsService _settingsService;
    private readonly Action<DownloadItem> _persist;
    private readonly IBandwidthLimiter _bandwidthLimiter;
    private readonly bool _markFromInternet;

    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly ConcurrentDictionary<Uri, Lazy<Task<byte[]>>> _keys = new();
    private readonly object _reportLock = new();
    private CancellationTokenSource? _cts;

    // One run's accounting, reset at the start of each download phase.
    private PieceWorker[] _workers = Array.Empty<PieceWorker>();
    private long _completedBytes;
    private long _completedMediaBytes;
    private int _completedMediaPieces;
    private int _totalMediaPieces;
    private long _transferred;
    private SpeedCalculator _speed = new();

    public DownloadItem Item { get; }

    public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    public event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<ConnectionsSnapshotEventArgs>? ConnectionsChanged;

    public StreamDownloadService(
        DownloadItem item,
        HttpClient client,
        IFileMerger fileMerger,
        ISettingsService settingsService,
        Action<DownloadItem> persist,
        IBandwidthLimiter? bandwidthLimiter = null,
        bool markFromInternet = true)
    {
        Item = item;
        _client = client;
        _fileMerger = fileMerger;
        _settingsService = settingsService;
        _persist = persist;
        _bandwidthLimiter = bandwidthLimiter ?? UnlimitedBandwidthLimiter.Instance;
        _markFromInternet = markFromInternet;
    }

    public async Task StartAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        CancellationTokenSource? cts = null;
        try
        {
            if (Item.Status is DownloadStatus.Downloading or DownloadStatus.Connecting) return;

            _cts?.Dispose();
            _cts = cts = new CancellationTokenSource();
            var token = cts.Token;
            SetStatus(DownloadStatus.Connecting);

            var source = Item.Stream ?? throw new InvalidOperationException("This download is not a stream.");
            if (string.IsNullOrWhiteSpace(Item.TempFolderPath))
                Item.TempFolderPath = Path.Combine(_settingsService.Current.TempFolder, Item.Id.ToString("N"));

            // Keys are fetched afresh each run: they are held only in memory, and a
            // key server that refused the last run may well accept this one.
            _keys.Clear();

            var state = StreamState.Read(Item.TempFolderPath);
            if (state is not { Downloaded: true })
            {
                var tracks = await ReadPlaylistsAsync(source, token).ConfigureAwait(false);

                if (state is null || !state.Describes(tracks))
                {
                    DiscardPieces(tracks.Count);
                    state = StreamState.For(tracks);
                    StreamState.Write(Item.TempFolderPath, state);
                }

                token.ThrowIfCancellationRequested();
                SetStatus(DownloadStatus.Downloading);
                await DownloadPiecesAsync(tracks, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                state = state with { Downloaded = true };
                StreamState.Write(Item.TempFolderPath, state);
            }

            await MergeAsync(state, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts?.IsCancellationRequested == true)
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

    // ------------------------------------------------------------ playlists --

    /// <summary>
    /// Reads the chosen quality's playlists again. Every run does: piece addresses
    /// are commonly signed and expire within hours, and the playlist is what hands
    /// out current ones.
    /// </summary>
    private async Task<IReadOnlyList<StreamTrack>> ReadPlaylistsAsync(StreamSource source, CancellationToken token)
    {
        var tracks = new List<StreamTrack>(2);

        if (source.Format == StreamFormat.Hls)
        {
            tracks.Add(StreamPlan.ForHls(await ReadHlsAsync(source.MainPlaylistUrl ?? Item.Url, token).ConfigureAwait(false)));
            if (source.AudioPlaylistUrl is { } audio)
                tracks.Add(StreamPlan.ForHls(await ReadHlsAsync(audio, token).ConfigureAwait(false)));
        }
        else
        {
            var (text, finalUri) = await FetchTextAsync(Item.Url, token).ConfigureAwait(false);
            var manifest = DashManifest.Parse(text, finalUri);
            tracks.Add(StreamPlan.ForDash(manifest, source.MainRepresentation ?? string.Empty));
            if (source.AudioRepresentation is { } audio)
                tracks.Add(StreamPlan.ForDash(manifest, audio));
        }

        return tracks;
    }

    private async Task<HlsPlaylist> ReadHlsAsync(string url, CancellationToken token)
    {
        var (text, finalUri) = await FetchTextAsync(url, token).ConfigureAwait(false);
        return HlsPlaylist.Parse(text, finalUri);
    }

    /// <summary>Retried like a connection: a playlist read is a request like any other.</summary>
    private Task<(string Text, Uri FinalUri)> FetchTextAsync(string url, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new FormatException("This download's address is not a web address.");
        var settings = _settingsService.Current;
        var options = Item.ToRequestOptions();

        return RetryPolicy.ExecuteAsync(
            (_, ct) => StreamFetch.GetTextAsync(_client, uri, options, ct),
            settings.MaxRetries,
            TimeSpan.FromMilliseconds(settings.RetryDelayMilliseconds),
            onRetry: null,
            token);
    }

    // --------------------------------------------------------------- pieces --

    private sealed record Job(int Track, int Index, StreamPiece Piece);

    private async Task DownloadPiecesAsync(IReadOnlyList<StreamTrack> tracks, CancellationToken token)
    {
        var settings = _settingsService.Current;
        var options = Item.ToRequestOptions();
        int workerCount = settings.ClampConnections(Item.ConnectionsCount);

        _completedBytes = 0;
        _completedMediaBytes = 0;
        _completedMediaPieces = 0;
        _transferred = 0;
        _speed = new SpeedCalculator();
        _totalMediaPieces = tracks.Sum(t => t.Pieces.Count(p => !p.IsInitialization));

        var jobs = new List<Job>();
        for (int t = 0; t < tracks.Count; t++)
        {
            Directory.CreateDirectory(TrackFolder(t));
            for (int i = 0; i < tracks[t].Pieces.Count; i++)
            {
                var piece = tracks[t].Pieces[i];
                var done = new FileInfo(PiecePath(t, i));
                if (done.Exists) RecordCompleted(piece, done.Length);
                else jobs.Add(new Job(t, i, piece));
            }
        }

        _workers = Enumerable.Range(0, workerCount).Select(id => new PieceWorker(id)).ToArray();
        var idle = new ConcurrentQueue<PieceWorker>(_workers);

        // The first piece to fail for good stops new pieces from starting, but the
        // ones already under way are let finish: each is progress a retry keeps.
        ExceptionDispatchInfo? failure = null;

        int interval = Math.Clamp(settings.ProgressUpdateIntervalMilliseconds, 50, 2000);
        using (new Timer(_ => Report(), null, interval, interval))
        {
            try
            {
                await Parallel.ForEachAsync(
                    jobs,
                    new ParallelOptions { MaxDegreeOfParallelism = workerCount, CancellationToken = token },
                    async (job, ct) =>
                    {
                        if (Volatile.Read(ref failure) is not null) return;

                        // Never empty: no more bodies run at once than there are workers.
                        idle.TryDequeue(out var worker);
                        worker ??= new PieceWorker(workerCount);
                        try
                        {
                            long length = await RetryPolicy.ExecuteAsync(
                                (_, attemptToken) => FetchPieceAsync(job, worker, options, settings, attemptToken),
                                settings.MaxRetries,
                                TimeSpan.FromMilliseconds(settings.RetryDelayMilliseconds),
                                onRetry: (attempt, ex) => worker.Retrying(attempt, ex.Message),
                                ct).ConfigureAwait(false);

                            lock (_reportLock)
                            {
                                RecordCompleted(job.Piece, length);
                                worker.Done();
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            worker.Failed(ex.Message);
                            Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(ex), null);
                        }
                        finally
                        {
                            idle.Enqueue(worker);
                        }
                    }).ConfigureAwait(false);
            }
            finally
            {
                Report();
            }
        }

        failure?.Throw();

        // Exact now that every piece is on disk.
        Item.TotalBytes = Interlocked.Read(ref _completedBytes);
        Item.DownloadedBytes = Item.TotalBytes;
    }

    private void RecordCompleted(StreamPiece piece, long length)
    {
        Interlocked.Add(ref _completedBytes, length);
        if (piece.IsInitialization) return;
        Interlocked.Add(ref _completedMediaBytes, length);
        Interlocked.Increment(ref _completedMediaPieces);
    }

    /// <summary>
    /// Fetches one piece into its <c>.part</c> file and renames it into place.
    /// </summary>
    /// <returns>The piece's length on disk.</returns>
    private async Task<long> FetchPieceAsync(
        Job job, PieceWorker worker, RequestOptions options, DownloadSettings settings, CancellationToken token)
    {
        worker.Sending();
        var piece = job.Piece;
        string path = PiecePath(job.Track, job.Index);
        string part = path + ".part";

        byte[]? key = piece.Key is null ? null : await GetKeyAsync(piece.Key.Uri, options, token).ConfigureAwait(false);

        // What is already in the .part, when it can be carried on from. Encrypted
        // bytes cannot be: the file holds plaintext, and CBC needs the ciphertext
        // block before the one it resumes at.
        long have = key is null && File.Exists(part) ? new FileInfo(part).Length : 0;
        if (piece.Range is { } bounds && have >= bounds.Length) have = 0;

        long firstByte = (piece.Range?.Offset ?? 0) + have;

        bool ranged = piece.Range is not null || have > 0;
        HttpRequestMessage Build()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, piece.Uri);
            HttpRequestDecorator.Apply(request, options);
            if (piece.Range is { } range) request.Headers.Range = new RangeHeaderValue(firstByte, range.End);
            else if (have > 0) request.Headers.Range = new RangeHeaderValue(firstByte, null);
            return request;
        }

        using var response = await AuthenticatedHttp.SendAsync(_client, Build, options, token).ConfigureAwait(false);
        StreamFetch.EnsureSuccess(response, options);

        // Checked before a byte is written, for the reason DownloadConnection
        // checks: a server that ignores a range says so only in its status line.
        long skip = 0;
        bool partial = response.StatusCode == HttpStatusCode.PartialContent;
        if (ranged)
        {
            if (partial && response.Content.Headers.ContentRange?.From is long from && from != firstByte)
                throw new IOException($"The server answered a request for byte {firstByte} with a range starting at {from}.");

            // The whole resource from byte zero: read past what is not wanted.
            if (!partial) skip = firstByte;
        }

        long? remaining = piece.Range is { } wanted
            ? wanted.Length - have
            : response.Content.Headers.ContentLength is long contentLength ? contentLength - skip : null;
        if (remaining < 0) throw new IOException("The server sent less than the piece it was asked for.");

        worker.Receiving(have, remaining is long left ? have + left : -1);

        int bufferSize = settings.ClampBufferSize();
        await using (var file = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, bufferSize, useAsync: true))
        {
            file.SetLength(have);
            file.Seek(have, SeekOrigin.Begin);

            await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            if (skip > 0) await SkipAsync(body, skip, bufferSize, token).ConfigureAwait(false);

            using var aes = key is null ? null : Aes.Create();
            var crypto = aes is null || key is null
                ? null
                : new CryptoStream(file, aes.CreateDecryptor(key, piece.Key!.Iv), CryptoStreamMode.Write, leaveOpen: true);
            Stream sink = crypto ?? (Stream)file;
            bool finished = false;

            try
            {
                var buffer = new byte[bufferSize];
                while (true)
                {
                    // Ask before reading, and return what was not used — the same
                    // contract every connection keeps with the speed limit.
                    int allowance = await _bandwidthLimiter.RequestAsync(buffer.Length, token).ConfigureAwait(false);
                    int want = remaining is long stillWanted ? (int)Math.Min(allowance, stillWanted) : allowance;

                    int read = want <= 0
                        ? 0
                        : await body.ReadAsync(buffer.AsMemory(0, want), token).ConfigureAwait(false);

                    if (read <= 0)
                    {
                        _bandwidthLimiter.Return(allowance);
                        break;
                    }
                    _bandwidthLimiter.Return(allowance - read);

                    await sink.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    if (remaining is not null) remaining -= read;
                    worker.Add(read);
                    Interlocked.Add(ref _transferred, read);
                }

                if (remaining > 0) throw new IOException($"The connection ended {remaining} bytes early.");

                // Throws on a wrong key or a damaged piece (the padding does not
                // check out). Retried: the next attempt starts the piece over.
                if (crypto is not null) await crypto.FlushFinalBlockAsync(token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false);
                finished = true;
            }
            finally
            {
                if (crypto is not null)
                {
                    // Disposing an unfinished CryptoStream finishes it, which throws for a
                    // partial block — and would bury the failure actually in flight.
                    try { await crypto.DisposeAsync().ConfigureAwait(false); }
                    catch when (!finished) { /* the exception already leaving is the one to report */ }
                }
            }
        }

        File.Move(part, path, overwrite: true);
        return new FileInfo(path).Length;
    }

    private static async Task SkipAsync(Stream body, long count, int bufferSize, CancellationToken token)
    {
        var buffer = new byte[bufferSize];
        while (count > 0)
        {
            int read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), token).ConfigureAwait(false);
            if (read <= 0) throw new IOException("The server sent less than the piece it was asked for.");
            count -= read;
        }
    }

    /// <summary>One fetch per key address however many pieces share it; a failed one is forgotten so the next piece asks again.</summary>
    private async Task<byte[]> GetKeyAsync(Uri uri, RequestOptions options, CancellationToken token)
    {
        var lazy = _keys.GetOrAdd(uri, address => new Lazy<Task<byte[]>>(() => FetchKeyAsync(address, options, token)));
        try
        {
            return await lazy.Value.WaitAsync(token).ConfigureAwait(false);
        }
        catch
        {
            _keys.TryRemove(new KeyValuePair<Uri, Lazy<Task<byte[]>>>(uri, lazy));
            throw;
        }
    }

    private async Task<byte[]> FetchKeyAsync(Uri uri, RequestOptions options, CancellationToken token)
    {
        HttpRequestMessage Build()
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            HttpRequestDecorator.Apply(request, options);
            return request;
        }

        using var response = await AuthenticatedHttp.SendAsync(_client, Build, options, token).ConfigureAwait(false);
        StreamFetch.EnsureSuccess(response, options);

        await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var key = await StreamFetch.ReadAtMostAsync(body, AesKeyBytes, token).ConfigureAwait(false);

        // A sign-in page or an error document where the key should be: decrypting
        // with it would "succeed" into noise, or fail every piece one at a time.
        return key is { Length: AesKeyBytes }
            ? key
            : throw new StreamNotSupportedException("The server did not hand out this video's decryption key.");
    }

    // ---------------------------------------------------------------- merge --

    private async Task MergeAsync(StreamState state, CancellationToken token)
    {
        if (Item.TotalBytes > 0) Item.DownloadedBytes = Item.TotalBytes;
        Item.MergeProgressPercentage = 0;
        SetStatus(DownloadStatus.Merging);

        var outputs = OutputPaths();
        int trackCount = state.PieceCounts.Count;

        for (int t = state.MergedTracks; t < trackCount; t++)
        {
            token.ThrowIfCancellationRequested();
            string folder = TrackFolder(t);
            string destination = outputs[t];

            int trackIndex = t;
            var expectedPieces = Enumerable.Range(0, state.PieceCounts[t]).Select(i => PiecePath(trackIndex, i)).ToList();

            // An interrupted merge carries on from its own plan, which names the pieces.
            IReadOnlyList<string> pieces = Array.Empty<string>();
            if (!_fileMerger.HasInterruptedMerge(folder))
            {
                pieces = expectedPieces;

                // Joined and moved into place by a run that was interrupted before it
                // could record so: nothing of this track is left but its output.
                if (!pieces.Any(File.Exists) && File.Exists(destination))
                {
                    state = state with { MergedTracks = t + 1 };
                    StreamState.Write(Item.TempFolderPath, state);
                    continue;
                }

                if (!pieces.All(File.Exists))
                {
                    StreamState.Write(Item.TempFolderPath, state with { Downloaded = false, MergedTracks = 0 });
                    throw new IOException("Part of this download is no longer on disk, so it has to be downloaded again. Click Retry.");
                }
            }

            long trackBytes = Math.Max(1, pieces.Count > 0 ? pieces.Sum(p => new FileInfo(p).Length) : Item.TotalBytes / trackCount);
            var progress = new InlineProgress(merged =>
            {
                double percentage = Math.Clamp((trackIndex + Math.Clamp(merged / (double)trackBytes, 0, 1)) * 100.0 / trackCount, 0, 100);
                Item.MergeProgressPercentage = percentage;
                RaiseProgress(0, TimeSpan.Zero, percentage);
            });

            try
            {
                await _fileMerger.MergeAsync(
                        folder, pieces, destination, _settingsService.Current.ClampBufferSize(), progress, token)
                    .ConfigureAwait(false);
            }
            catch (IOException) when (!token.IsCancellationRequested
                                      && !_fileMerger.HasInterruptedMerge(folder)
                                      && !expectedPieces.All(File.Exists))
            {
                // The merger gave up on its plan because a piece went missing. The
                // next run fetches what is missing before it merges again.
                StreamState.Write(Item.TempFolderPath, state with { Downloaded = false, MergedTracks = 0 });
                throw;
            }

            _fileMerger.CleanupChunks(pieces, folder);
            state = state with { MergedTracks = t + 1 };
            StreamState.Write(Item.TempFolderPath, state);
        }

        StreamState.Delete(Item.TempFolderPath);
        try
        {
            if (Directory.Exists(Item.TempFolderPath)) Directory.Delete(Item.TempFolderPath, recursive: true);
        }
        catch { /* best-effort — the startup sweep collects it */ }

        if (_markFromInternet)
        {
            foreach (string output in outputs.Take(trackCount))
                MarkOfTheWeb.Apply(output, Item.Url, ReferrerOf(Item));
        }

        long total = outputs.Take(trackCount).Sum(p => File.Exists(p) ? new FileInfo(p).Length : 0);
        if (total > 0) Item.TotalBytes = total;

        if (FileChecksum.FromStored(Item.ExpectedChecksum) is { } expected)
        {
            Item.MergeProgressPercentage = 0;
            SetStatus(DownloadStatus.Verifying);
            Item.ChecksumResult = await FileChecksum.VerifyAsync(outputs[0], expected, null, token).ConfigureAwait(false);
            if (Item.ChecksumResult != ChecksumResult.Match)
            {
                SetStatus(DownloadStatus.Failed,
                    $"The finished file does not match its {FileChecksum.Name(expected.Algorithm)} checksum. " +
                    "It is damaged or not the file that was published.");
                return;
            }
        }

        Item.MergeProgressPercentage = 100;
        Item.DownloadedBytes = Item.TotalBytes;
        Item.CompletedAt = DateTimeOffset.Now;
        SetStatus(DownloadStatus.Completed);
    }

    /// <summary>Where each track is saved: the download's own file, then the separate sound beside it.</summary>
    private IReadOnlyList<string> OutputPaths()
    {
        var paths = new List<string> { Item.FullPath };
        if (Item.AudioFullPath is { } audio) paths.Add(audio);
        else if (Item.Stream?.HasSeparateAudio == true)
            paths.Add(Path.Combine(Item.SaveFolder, $"{Path.GetFileNameWithoutExtension(Item.FileName)}.audio{Item.Stream.AudioExtension ?? ".m4a"}"));
        return paths;
    }

    private string TrackFolder(int track) => Path.Combine(Item.TempFolderPath, track == 0 ? "main" : "audio");

    private string PiecePath(int track, int index) => Path.Combine(TrackFolder(track), $"{index:D6}.seg");

    private void DiscardPieces(int trackCount)
    {
        var outputs = OutputPaths();
        for (int t = 0; t < Math.Max(2, trackCount); t++)
        {
            string folder = TrackFolder(t);
            _fileMerger.DiscardInterruptedMerge(folder, t < outputs.Count ? outputs[t] : string.Empty);
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch { /* best-effort — pieces the new playlist names are overwritten anyway */ }
        }
        StreamState.Delete(Item.TempFolderPath);
        Item.DownloadedBytes = 0;
        Item.MergeProgressPercentage = 0;
    }

    // ------------------------------------------------------------- progress --

    private void Report()
    {
        lock (_reportLock)
        {
            var workers = _workers;
            long downloaded = Interlocked.Read(ref _completedBytes) + workers.Sum(w => w.Bytes);

            // The size of a stream is only known once it is down: until then, the
            // pieces still to come are taken to be the size of those already here.
            long total = 0;
            int donePieces = _completedMediaPieces;
            if (donePieces > 0)
            {
                long average = Interlocked.Read(ref _completedMediaBytes) / donePieces;
                total = Interlocked.Read(ref _completedBytes) + average * Math.Max(0, _totalMediaPieces - donePieces);
            }
            total = Math.Max(total, downloaded);

            _speed.AddSample(Interlocked.Read(ref _transferred));
            double speed = _speed.GetSpeedBytesPerSecond();

            Item.DownloadedBytes = downloaded;
            Item.TotalBytes = donePieces > 0 ? total : 0;
            Item.CurrentSpeedBytesPerSecond = speed;
            Item.EstimatedTimeRemaining = SpeedCalculator.EstimateTimeRemaining(downloaded, Item.TotalBytes, speed);

            RaiseProgress(speed, Item.EstimatedTimeRemaining, null);
            ConnectionsChanged?.Invoke(this, new ConnectionsSnapshotEventArgs
            {
                DownloadId = Item.Id,
                Connections = workers.Select(w => w.Snapshot()).ToList()
            });
        }
    }

    private void RaiseProgress(double speed, TimeSpan? remaining, double? mergePercentage) =>
        ProgressChanged?.Invoke(this, new DownloadProgressEventArgs
        {
            DownloadId = Item.Id,
            DownloadedBytes = Item.DownloadedBytes,
            TotalBytes = Item.TotalBytes,
            SpeedBytesPerSecond = speed,
            EstimatedTimeRemaining = remaining,
            MergePercentage = mergePercentage
        });

    /// <summary>
    /// One of the download's parallel slots, as the details window's connection list
    /// shows it: the piece it is on, and how far through.
    /// </summary>
    private sealed class PieceWorker
    {
        private readonly int _id;
        private long _bytes;
        private long _length = -1;
        private volatile ConnectionStatus _status = ConnectionStatus.Idle;
        private volatile int _retryCount;
        private volatile string? _lastError;

        public PieceWorker(int id) => _id = id;

        public long Bytes => Interlocked.Read(ref _bytes);

        public void Sending() => _status = ConnectionStatus.SendingRequest;

        public void Receiving(long have, long length)
        {
            Interlocked.Exchange(ref _bytes, have);
            Interlocked.Exchange(ref _length, length);
            _status = ConnectionStatus.ReceivingData;
        }

        public void Add(int count) => Interlocked.Add(ref _bytes, count);

        public void Retrying(int attempt, string message)
        {
            Interlocked.Exchange(ref _bytes, 0);
            _retryCount = attempt;
            _lastError = message;
            _status = ConnectionStatus.SendingRequest;
        }

        /// <summary>The piece's bytes now count as completed, so they stop counting here.</summary>
        public void Done()
        {
            Interlocked.Exchange(ref _bytes, 0);
            Interlocked.Exchange(ref _length, -1);
            _retryCount = 0;
            _status = ConnectionStatus.Idle;
        }

        public void Failed(string message)
        {
            _lastError = message;
            _status = ConnectionStatus.Failed;
        }

        public ConnectionInfo Snapshot() => new()
        {
            ConnectionId = _id,
            RangeStart = 0,
            RangeEnd = Interlocked.Read(ref _length) - 1,
            BytesDownloaded = Bytes,
            Status = _status,
            RetryCount = _retryCount,
            LastError = _lastError
        };
    }

    // ------------------------------------------------------------ lifecycle --

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
        if (Item.Status == DownloadStatus.Completed) return;

        SetStatus(DownloadStatus.Cancelled);

        var outputs = OutputPaths();
        for (int t = 0; t < outputs.Count; t++)
            _fileMerger.DiscardInterruptedMerge(TrackFolder(t), outputs[t]);
        ScheduleTempFolderDeletion();
    }

    public async Task RetryAsync()
    {
        Item.ErrorMessage = null;
        await StartAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Not offered for a stream (<see cref="DownloadActions.CanRefreshAddress"/>).
    /// A fresh playlist is read on every run anyway, and a different playlist is a
    /// different cut of the video, which no size comparison could vouch for.
    /// </summary>
    public bool ChangeAddress(AddressChange change) => false;

    /// <summary>See <see cref="DownloadService"/>'s twin: the pieces are still open when Stop returns.</summary>
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
                catch { /* best-effort — a piece is still open */ }

                await Task.Delay(TempDeleteRetryDelayMilliseconds).ConfigureAwait(false);
            }
        });
    }

    private static string? ReferrerOf(DownloadItem item)
    {
        foreach (var (name, value) in item.Headers)
        {
            if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase)) return value;
        }
        return null;
    }

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

    /// <summary>Runs its callback inline, in order — see the note in <see cref="DownloadService"/>.</summary>
    private sealed class InlineProgress : IProgress<long>
    {
        private readonly Action<long> _report;

        public InlineProgress(Action<long> report) => _report = report;

        public void Report(long value) => _report(value);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _lifecycleLock.Dispose();
    }
}
