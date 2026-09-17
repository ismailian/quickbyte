using System.Security.Cryptography;
using System.Text;
using QuickByte.Core.Enums;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.Core.Services;
using QuickByte.Core.Streaming;

namespace QuickByte.Core.Tests.Streaming;

/// <summary>
/// Streams downloaded end to end, against playlists, pieces and keys served from
/// memory, and checked against the exact bytes the pieces were made from. A file
/// that is merely the right length proves nothing here: a piece decrypted with the
/// wrong IV, joined out of order or resumed at the wrong offset is exactly as long.
/// </summary>
public sealed class StreamDownloadServiceTests
{
    private const string Root = "https://cdn.example.com/show/";

    [Fact]
    public async Task An_encrypted_hls_video_and_its_separate_sound_are_saved_decrypted_and_in_order()
    {
        using var fixture = new Fixture();
        byte[] key = RandomNumberGenerator.GetBytes(16);
        byte[] explicitIv = RandomNumberGenerator.GetBytes(16);
        fixture.Web.Add(Root + "key.bin", key);

        // Pieces 0 and 1 under the explicit IV; 2 under its sequence number (12).
        var video = Pieces(3, 5000);
        fixture.Web.Add(Root + "v0.ts", Encrypt(video[0], key, explicitIv));
        fixture.Web.Add(Root + "v1.ts", Encrypt(video[1], key, explicitIv));
        fixture.Web.Add(Root + "v2.ts", Encrypt(video[2], key, SequenceIv(12)));
        fixture.Web.Add(Root + "video.m3u8", $"""
            #EXTM3U
            #EXT-X-MEDIA-SEQUENCE:10
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x{Convert.ToHexString(explicitIv)}
            #EXTINF:4,
            v0.ts
            #EXTINF:4,
            v1.ts
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin"
            #EXTINF:4,
            v2.ts
            #EXT-X-ENDLIST
            """);

        byte[] init = Encoding.ASCII.GetBytes("ftyp-init-section");
        var audio = Pieces(2, 3000);
        fixture.Web.Add(Root + "a-init.mp4", init);
        fixture.Web.Add(Root + "a0.m4s", audio[0]);
        fixture.Web.Add(Root + "a1.m4s", audio[1]);
        fixture.Web.Add(Root + "audio.m3u8", """
            #EXTM3U
            #EXT-X-MAP:URI="a-init.mp4"
            #EXTINF:4,
            a0.m4s
            #EXTINF:4,
            a1.m4s
            #EXT-X-ENDLIST
            """);

        var service = fixture.Create(new StreamSource
        {
            Format = StreamFormat.Hls,
            MainPlaylistUrl = Root + "video.m3u8",
            AudioPlaylistUrl = Root + "audio.m3u8",
            AudioFileName = "Talk.audio.m4a"
        }, fileName: "Talk.ts");

        await service.StartAsync();

        Assert.Equal(
            new[] { DownloadStatus.Connecting, DownloadStatus.Downloading, DownloadStatus.Merging, DownloadStatus.Completed },
            fixture.Statuses);
        Assert.Equal(Concat(video), File.ReadAllBytes(service.Item.FullPath));
        Assert.Equal(Concat(new[] { init }.Concat(audio)), File.ReadAllBytes(service.Item.AudioFullPath!));
        Assert.Equal(new FileInfo(service.Item.FullPath).Length + new FileInfo(service.Item.AudioFullPath!).Length, service.Item.TotalBytes);
        Assert.False(Directory.Exists(service.Item.TempFolderPath));

        // One key request however many pieces share the key.
        Assert.Equal(1, fixture.Web.CountRequests(Root + "key.bin"));
    }

    [Fact]
    public async Task A_retry_fetches_only_the_pieces_that_are_missing()
    {
        using var fixture = new Fixture();
        var pieces = Pieces(4, 2000);
        AddTsPlaylist(fixture.Web, "index.m3u8", pieces);
        fixture.Web.Remove(Root + "p2.ts");

        var service = fixture.Create(Hls("index.m3u8"));
        await service.StartAsync();

        Assert.Equal(DownloadStatus.Failed, service.Item.Status);
        Assert.Contains("404", service.Item.ErrorMessage);

        fixture.Web.Add(Root + "p2.ts", pieces[2]);
        await service.RetryAsync();

        Assert.Equal(DownloadStatus.Completed, service.Item.Status);
        Assert.Equal(Concat(pieces), File.ReadAllBytes(service.Item.FullPath));
        foreach (int i in new[] { 0, 1, 3 })
            Assert.Equal(1, fixture.Web.CountRequests(Root + $"p{i}.ts"));
    }

    [Fact]
    public async Task A_piece_whose_connection_ends_early_carries_on_from_what_arrived()
    {
        using var fixture = new Fixture(maxRetries: 2);
        var pieces = Pieces(2, 10_000);
        AddTsPlaylist(fixture.Web, "index.m3u8", pieces);
        fixture.Web.CutShort(Root + "p1.ts", 4096);

        var service = fixture.Create(Hls("index.m3u8"));
        await service.StartAsync();

        Assert.Equal(DownloadStatus.Completed, service.Item.Status);
        Assert.Equal(Concat(pieces), File.ReadAllBytes(service.Item.FullPath));
        Assert.Contains(fixture.Web.Requests, r => r.Url == Root + "p1.ts" && r.Range == "bytes=4096-");
    }

    [Fact]
    public async Task An_encrypted_piece_whose_connection_ends_early_starts_again_rather_than_resuming()
    {
        // What is on disk is plaintext; carrying on would decrypt the rest of the
        // piece against the wrong CBC block and write noise into the video.
        using var fixture = new Fixture(maxRetries: 2);
        byte[] key = RandomNumberGenerator.GetBytes(16);
        var pieces = Pieces(2, 10_000);
        fixture.Web.Add(Root + "key.bin", key);
        fixture.Web.Add(Root + "p0.ts", Encrypt(pieces[0], key, SequenceIv(0)));
        fixture.Web.Add(Root + "p1.ts", Encrypt(pieces[1], key, SequenceIv(1)));
        fixture.Web.Add(Root + "index.m3u8", "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXTINF:4,\np0.ts\n#EXTINF:4,\np1.ts\n#EXT-X-ENDLIST\n");
        fixture.Web.CutShort(Root + "p1.ts", 4096);

        var service = fixture.Create(Hls("index.m3u8"));
        await service.StartAsync();

        Assert.Equal(DownloadStatus.Completed, service.Item.Status);
        Assert.Equal(Concat(pieces), File.ReadAllBytes(service.Item.FullPath));
        Assert.All(fixture.Web.Requests.Where(r => r.Url == Root + "p1.ts"), r => Assert.Null(r.Range));
    }

    [Fact]
    public async Task Byte_range_pieces_are_cut_correctly_even_from_a_server_that_ignores_ranges()
    {
        using var fixture = new Fixture();
        byte[] whole = RandomNumberGenerator.GetBytes(9000);
        fixture.Web.Add(Root + "all.ts", whole, honoursRanges: false);
        fixture.Web.Add(Root + "index.m3u8", """
            #EXTM3U
            #EXTINF:4,
            #EXT-X-BYTERANGE:3000@1000
            all.ts
            #EXTINF:4,
            #EXT-X-BYTERANGE:2500
            all.ts
            #EXT-X-ENDLIST
            """);

        var service = fixture.Create(Hls("index.m3u8"));
        await service.StartAsync();

        Assert.Equal(DownloadStatus.Completed, service.Item.Status);
        Assert.Equal(whole[1000..6500], File.ReadAllBytes(service.Item.FullPath));
    }

    [Fact]
    public async Task A_dash_video_and_its_sound_are_saved_from_their_templates()
    {
        using var fixture = new Fixture();
        var video = Pieces(2, 4000);
        var audio = Pieces(2, 1000);
        fixture.Web.Add(Root + "v/init.mp4", Encoding.ASCII.GetBytes("video-init"));
        fixture.Web.Add(Root + "v/1.m4s", video[0]);
        fixture.Web.Add(Root + "v/2.m4s", video[1]);
        fixture.Web.Add(Root + "a/init.mp4", Encoding.ASCII.GetBytes("audio-init"));
        fixture.Web.Add(Root + "a/1.m4s", audio[0]);
        fixture.Web.Add(Root + "a/2.m4s", audio[1]);
        fixture.Web.Add(Root + "manifest.mpd", """
            <MPD mediaPresentationDuration="PT8S"><Period>
              <AdaptationSet mimeType="video/mp4">
                <SegmentTemplate duration="4" initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/$Number$.m4s"/>
                <Representation id="v" bandwidth="1"/>
              </AdaptationSet>
              <AdaptationSet mimeType="audio/mp4">
                <SegmentTemplate duration="4" initialization="$RepresentationID$/init.mp4" media="$RepresentationID$/$Number$.m4s"/>
                <Representation id="a" bandwidth="1"/>
              </AdaptationSet>
            </Period></MPD>
            """, "application/dash+xml");

        var service = fixture.Create(
            new StreamSource { Format = StreamFormat.Dash, MainRepresentation = "v", AudioRepresentation = "a", AudioFileName = "clip.audio.m4a" },
            fileName: "clip.mp4", url: Root + "manifest.mpd");
        await service.StartAsync();

        Assert.Equal(DownloadStatus.Completed, service.Item.Status);
        Assert.Equal(Concat(new[] { Encoding.ASCII.GetBytes("video-init") }.Concat(video)), File.ReadAllBytes(service.Item.FullPath));
        Assert.Equal(Concat(new[] { Encoding.ASCII.GetBytes("audio-init") }.Concat(audio)), File.ReadAllBytes(service.Item.AudioFullPath!));
    }

    [Fact]
    public async Task Pieces_from_a_playlist_that_has_since_changed_shape_are_thrown_away()
    {
        using var fixture = new Fixture();
        var before = Pieces(3, 1500);
        AddTsPlaylist(fixture.Web, "index.m3u8", before);
        fixture.Web.Remove(Root + "p2.ts");

        var service = fixture.Create(Hls("index.m3u8"));
        await service.StartAsync();
        Assert.Equal(DownloadStatus.Failed, service.Item.Status);

        // A re-cut of the video: two pieces, different bytes, same names.
        var after = Pieces(2, 1800);
        AddTsPlaylist(fixture.Web, "index.m3u8", after);
        await service.RetryAsync();

        Assert.Equal(DownloadStatus.Completed, service.Item.Status);
        Assert.Equal(Concat(after), File.ReadAllBytes(service.Item.FullPath));
    }

    [Fact]
    public async Task A_download_whose_pieces_are_all_down_finishes_without_its_playlist()
    {
        // The merge failing (here, for want of access) must not send the next run
        // back to a playlist that may well have expired by then.
        using var fixture = new Fixture();
        var pieces = Pieces(3, 1200);
        AddTsPlaylist(fixture.Web, "index.m3u8", pieces);
        fixture.Merger.FailNextMerge = true;

        var service = fixture.Create(Hls("index.m3u8"));
        await service.StartAsync();
        Assert.Equal(DownloadStatus.Failed, service.Item.Status);

        fixture.Web.Remove(Root + "index.m3u8");
        await service.RetryAsync();

        Assert.Equal(DownloadStatus.Completed, service.Item.Status);
        Assert.Equal(Concat(pieces), File.ReadAllBytes(service.Item.FullPath));
        Assert.Equal(1, fixture.Web.CountRequests(Root + "index.m3u8"));
    }

    [Fact]
    public async Task A_key_server_that_answers_with_something_other_than_a_key_fails_the_download_plainly()
    {
        using var fixture = new Fixture(maxRetries: 3);
        fixture.Web.Add(Root + "key.bin", Encoding.ASCII.GetBytes("<html>sign in</html>"), "text/html");
        fixture.Web.Add(Root + "p0.ts", new byte[32]);
        fixture.Web.Add(Root + "index.m3u8", "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXTINF:4,\np0.ts\n#EXT-X-ENDLIST\n");

        var service = fixture.Create(Hls("index.m3u8"));
        await service.StartAsync();

        Assert.Equal(DownloadStatus.Failed, service.Item.Status);
        Assert.Contains("decryption key", service.Item.ErrorMessage);
        Assert.Equal(1, fixture.Web.CountRequests(Root + "key.bin")); // not retried
    }

    [Fact]
    public async Task A_live_stream_fails_with_the_reason_rather_than_downloading_forever()
    {
        using var fixture = new Fixture();
        fixture.Web.Add(Root + "index.m3u8", "#EXTM3U\n#EXTINF:4,\np0.ts\n");

        var service = fixture.Create(Hls("index.m3u8"));
        await service.StartAsync();

        Assert.Equal(DownloadStatus.Failed, service.Item.Status);
        Assert.Equal(StreamPlan.LiveMessage, service.Item.ErrorMessage);
    }

    [Fact]
    public async Task Pieces_are_fetched_with_the_downloads_cookies()
    {
        using var fixture = new Fixture();
        AddTsPlaylist(fixture.Web, "index.m3u8", Pieces(2, 100));

        var service = fixture.Create(Hls("index.m3u8"));
        service.Item.Headers = new Dictionary<string, string> { ["Cookie"] = "session=7" };
        await service.StartAsync();

        Assert.All(fixture.Web.Requests, r => Assert.Equal("session=7", r.Cookie));
    }

    // ------------------------------------------------------------- plumbing --

    private static StreamSource Hls(string playlist) =>
        new() { Format = StreamFormat.Hls, MainPlaylistUrl = Root + playlist };

    private static void AddTsPlaylist(StubWeb web, string name, IReadOnlyList<byte[]> pieces)
    {
        var text = new StringBuilder("#EXTM3U\n");
        for (int i = 0; i < pieces.Count; i++)
        {
            web.Add(Root + $"p{i}.ts", pieces[i]);
            text.Append("#EXTINF:4,\n").Append($"p{i}.ts\n");
        }
        web.Add(Root + name, text.Append("#EXT-X-ENDLIST\n").ToString());
    }

    private static byte[][] Pieces(int count, int size) =>
        Enumerable.Range(0, count).Select(_ => RandomNumberGenerator.GetBytes(size + Random.Shared.Next(0, 300))).ToArray();

    private static byte[] Concat(IEnumerable<byte[]> parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Encrypt(byte[] plain, byte[] key, byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        return aes.EncryptCbc(plain, iv);
    }

    private static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        for (int i = 15; i >= 8; i--, sequence >>= 8) iv[i] = (byte)(sequence & 0xFF);
        return iv;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly TempFolder _root = new();
        private readonly List<StreamDownloadService> _services = new();

        public Fixture(int maxRetries = 0)
        {
            Settings = new FakeSettings(new DownloadSettings
            {
                TempFolder = Path.Combine(_root.Path, "temp"),
                DefaultDownloadFolder = Path.Combine(_root.Path, "downloads"),
                MaxRetries = maxRetries,
                RetryDelayMilliseconds = 1,
                ProgressUpdateIntervalMilliseconds = 50
            });
            Directory.CreateDirectory(Settings.Current.DefaultDownloadFolder);
        }

        public StubWeb Web { get; } = new();
        public FakeSettings Settings { get; }
        public FlakyMerger Merger { get; } = new();
        public List<DownloadStatus> Statuses { get; } = new();

        public StreamDownloadService Create(StreamSource source, string fileName = "video.ts", string? url = null)
        {
            var item = new DownloadItem
            {
                Url = url ?? source.MainPlaylistUrl ?? string.Empty,
                FileName = fileName,
                SaveFolder = Settings.Current.DefaultDownloadFolder,
                ConnectionsCount = 4,
                SupportsResume = true,
                Stream = source
            };

            var service = new StreamDownloadService(item, Web.Client, Merger, Settings, _ => { }, markFromInternet: false);
            service.StatusChanged += (_, e) => Statuses.Add(e.NewStatus);
            _services.Add(service);
            return service;
        }

        public void Dispose()
        {
            foreach (var service in _services) service.Dispose();
            Web.Dispose();
            _root.Dispose();
        }
    }

    /// <summary>The real merger, which can be told to refuse once.</summary>
    private sealed class FlakyMerger : IFileMerger
    {
        private readonly FileMerger _inner = new();

        public bool FailNextMerge { get; set; }

        public Task MergeAsync(string tempFolder, IReadOnlyList<string> orderedChunkPaths, string destinationFilePath,
            int bufferSize, IProgress<long>? bytesMergedProgress, CancellationToken cancellationToken)
        {
            if (FailNextMerge)
            {
                FailNextMerge = false;
                throw new UnauthorizedAccessException("Access to the destination is denied.");
            }
            return _inner.MergeAsync(tempFolder, orderedChunkPaths, destinationFilePath, bufferSize, bytesMergedProgress, cancellationToken);
        }

        public bool HasInterruptedMerge(string tempFolder) => _inner.HasInterruptedMerge(tempFolder);

        public void DiscardInterruptedMerge(string tempFolder, string destinationFilePath) =>
            _inner.DiscardInterruptedMerge(tempFolder, destinationFilePath);

        public void CleanupChunks(IReadOnlyList<string> chunkPaths, string tempFolder) =>
            _inner.CleanupChunks(chunkPaths, tempFolder);
    }

    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(DownloadSettings settings) => Current = settings;

        public DownloadSettings Current { get; private set; }
        public event EventHandler<DownloadSettings>? SettingsChanged;

        public void Load() { }

        public void Save(DownloadSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(this, settings);
        }
    }
}
