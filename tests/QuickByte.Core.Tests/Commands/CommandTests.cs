using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using QuickByte.Core.Commands;
using QuickByte.Core.Enums;
using QuickByte.Core.Events;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.Core.Services;
using QuickByte.Core.Streaming;

namespace QuickByte.Core.Tests.Commands;

/// <summary>
/// qb's grammar. A script depends on every one of these readings staying put, and a
/// usage mistake has to be caught here — before anything is sent to QuickByte.
/// </summary>
public sealed class CommandLineTests
{
    private const string Cwd = @"C:\Users\me\Videos";

    private static ParsedCommandLine Parse(params string[] args) => CommandLine.Parse(args, Cwd);

    [Theory]
    [InlineData]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void Nothing_or_a_request_for_help_shows_the_usage(params string[] args)
    {
        Assert.True(Parse(args).ShowHelp);
    }

    [Fact]
    public void An_add_reads_its_links_and_every_option()
    {
        var parsed = Parse("add", "https://example.com/a.iso", "-o", "Downloads", "-n", "disc.iso", "-c", "16",
            "--checksum", "sha256:ab", "--quality", "720p", "--json");

        var request = parsed.Request!;
        Assert.Null(parsed.Error);
        Assert.True(parsed.Json);
        Assert.Equal(CommandVerb.Add, request.Verb);
        Assert.Equal(new[] { "https://example.com/a.iso" }, request.Urls);
        Assert.Equal(@"C:\Users\me\Videos\Downloads", request.Folder);
        Assert.Equal("disc.iso", request.FileName);
        Assert.Equal(16, request.Connections);
        Assert.Equal("sha256:ab", request.Checksum);
        Assert.Equal("720p", request.Quality);
        Assert.False(request.Paused);
    }

    [Fact]
    public void A_folder_given_in_full_is_kept_and_environment_variables_are_expanded()
    {
        Assert.Equal(@"D:\Iso", Parse("add", "https://x/a", "--folder", @"D:\Iso").Request!.Folder);

        string expanded = Parse("add", "https://x/a", "--folder", @"%SystemRoot%\Temp").Request!.Folder!;
        Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "Temp"), expanded);
    }

    [Fact]
    public void Several_links_go_into_a_queue_or_are_left_paused()
    {
        var queued = Parse("add", "https://x/1", "https://x/2", "--queue", "Night").Request!;
        Assert.Equal(2, queued.Urls.Count);
        Assert.Equal("Night", queued.Queue);

        Assert.True(Parse("add", "https://x/1", "--paused").Request!.Paused);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("add", "--paused")]
    [InlineData("add", "https://x/1", "https://x/2", "--name", "a.iso")]
    [InlineData("add", "https://x/1", "https://x/2", "--checksum", "abc")]
    [InlineData("add", "https://x/1", "--connections", "0")]
    [InlineData("add", "https://x/1", "--connections", "64")]
    [InlineData("add", "https://x/1", "--connections", "many")]
    [InlineData("add", "https://x/1", "--folder")]
    [InlineData("add", "https://x/1", "--speed", "5")]
    [InlineData("add", "https://x/1", "--queue", "Night", "--paused")]
    [InlineData("list", "--status", "sleeping")]
    [InlineData("list", "extra")]
    [InlineData("pause")]
    [InlineData("pause", "a", "b")]
    [InlineData("queue", "rename", "Night")]
    [InlineData("queue", "start")]
    [InlineData("status", "now")]
    [InlineData("download", "https://x/1")]
    public void A_usage_mistake_is_caught_before_anything_is_sent(params string[] args)
    {
        var parsed = Parse(args);

        Assert.NotNull(parsed.Error);
        Assert.Null(parsed.Request);
    }

    [Fact]
    public void Reading_commands_take_their_target_or_filter()
    {
        Assert.Equal("completed", Parse("list", "--status", "COMPLETED").Request!.Filter);
        Assert.Equal("all", Parse("ls").Request!.Filter);
        Assert.Equal(("3f9c", CommandVerb.Pause), (Parse("pause", "3f9c").Request!.Target, Parse("pause", "3f9c").Request!.Verb));
        Assert.Equal("all", Parse("resume", "all").Request!.Target);
    }

    [Fact]
    public void A_queue_name_with_spaces_survives_not_being_quoted()
    {
        var request = Parse("queue", "stop", "Late", "night", "films").Request!;

        Assert.Equal((CommandVerb.QueueStop, "Late night films"), (request.Verb, request.Queue));
    }

    [Fact]
    public void Only_commands_that_add_work_start_QuickByte()
    {
        Assert.True(CommandLine.StartsQuickByte(Parse("add", "https://x/1").Request!));
        Assert.True(CommandLine.StartsQuickByte(Parse("queue", "start", "Night").Request!));
        Assert.False(CommandLine.StartsQuickByte(Parse("list").Request!));
        Assert.False(CommandLine.StartsQuickByte(Parse("pause", "all").Request!));
        Assert.False(CommandLine.StartsQuickByte(Parse("status").Request!));
    }

    [Fact]
    public void The_version_is_asked_for_in_the_usual_ways()
    {
        Assert.True(Parse("--version").ShowVersion);
        Assert.True(Parse("version").ShowVersion);
    }
}

/// <summary>
/// Commands carried out against the managers the windows use: a download added
/// from a prompt is probed, named, filed and started by the same rules.
/// </summary>
public sealed class CommandProcessorTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly FakeDownloads _downloads = new();
    private readonly QueueManager _queues;
    private readonly FakeProbe _probe = new();
    private readonly FakeStreams _streams = new();
    private readonly FakeSettings _settings;
    private readonly List<DownloadRequest> _added = new();
    private readonly CommandProcessor _processor;

    public CommandProcessorTests()
    {
        _settings = new FakeSettings(new DownloadSettings { DefaultDownloadFolder = _folder.Path, DefaultConnectionsCount = 8 });
        _queues = new QueueManager(new MemoryQueues(), _downloads, new InlineDispatcher());
        _queues.Load();
        _processor = new CommandProcessor(_downloads, _queues, _probe, _streams, _settings, request =>
        {
            _added.Add(request);
            return Task.FromResult(_downloads.Add(new DownloadItem
            {
                Url = request.Url, FileName = request.FileName, SaveFolder = request.SaveFolder, Status = DownloadStatus.Queued
            }));
        }, "1.22.0");
    }

    public void Dispose()
    {
        _queues.Dispose();
        _folder.Dispose();
    }

    private Task<CommandResponse> Run(CommandRequest request) => _processor.ExecuteAsync(request);

    [Fact]
    public async Task An_added_link_is_probed_named_placed_as_Options_says_and_started()
    {
        _probe.Info = new RemoteFileInfo { FileName = "ubuntu.iso", SupportsRangeRequests = true, ContentLength = 100 };

        var response = await Run(new CommandRequest { Verb = CommandVerb.Add, Urls = { "https://example.com/get?id=1" } });

        Assert.True(response.Ok, response.Error);
        var request = Assert.Single(_added);
        Assert.Equal(("https://example.com/get?id=1", "ubuntu.iso", _folder.Path, 8), (request.Url, request.FileName, request.SaveFolder, request.ConnectionsCount));
        Assert.False(request.StartImmediately); // started separately, so the command returns at once
        Assert.Equal(new[] { _downloads.Downloads.Single().Id }, _downloads.Started);
        Assert.Equal("Added 1 download, started.", response.Message);
    }

    [Fact]
    public async Task A_login_in_the_link_is_lifted_out_of_the_address_and_carried_as_credentials()
    {
        await Run(new CommandRequest { Verb = CommandVerb.Add, Urls = { "ftp://alice:s3cret@files.example.com/a.bin" } });

        var request = Assert.Single(_added);
        Assert.DoesNotContain("s3cret", request.Url);
        Assert.Equal(("alice", "s3cret"), (request.Credentials!.UserName, request.Credentials.Password));
        Assert.Equal("alice", _probe.LastOptions!.Credentials!.UserName);
    }

    [Fact]
    public async Task A_server_that_will_not_split_the_file_gets_one_connection_whatever_was_asked()
    {
        _probe.Info = new RemoteFileInfo { FileName = "a.bin", SupportsRangeRequests = false };

        await Run(new CommandRequest { Verb = CommandVerb.Add, Urls = { "https://example.com/a.bin" }, Connections = 16 });

        Assert.Equal(1, _added.Single().ConnectionsCount);
    }

    [Fact]
    public async Task Links_for_a_queue_join_it_and_wait_and_the_queue_is_created_if_missing()
    {
        var response = await Run(new CommandRequest { Verb = CommandVerb.Add, Urls = { "https://x.example.com/1.zip", "https://x.example.com/2.zip" }, Queue = "Night" });

        var queue = Assert.Single(_queues.Queues);
        Assert.Equal("Night", queue.Name);
        Assert.Equal(_downloads.Downloads.Select(d => d.Id).OrderBy(i => i), queue.ItemIds.OrderBy(i => i));
        Assert.Empty(_downloads.Started);
        Assert.Equal("Added 2 downloads, in queue \"Night\".", response.Message);

        await Run(new CommandRequest { Verb = CommandVerb.Add, Urls = { "https://x.example.com/3.zip" }, Queue = "NIGHT" });
        Assert.Single(_queues.Queues);
        Assert.Equal(3, _queues.Queues.Single().ItemIds.Count);
    }

    [Fact]
    public async Task A_paused_add_is_not_started()
    {
        await Run(new CommandRequest { Verb = CommandVerb.Add, Urls = { "https://x.example.com/1.zip" }, Paused = true });

        Assert.Single(_added);
        Assert.Empty(_downloads.Started);
    }

    [Fact]
    public async Task One_bad_link_fails_the_command_without_losing_the_good_ones()
    {
        var response = await Run(new CommandRequest { Verb = CommandVerb.Add, Urls = { "https://x.example.com/1.zip", "file:///C:/secret.txt" } });

        Assert.False(response.Ok);
        Assert.Single(response.Downloads!);
        Assert.Contains("file:///C:/secret.txt", response.Error);
    }

    [Fact]
    public async Task A_link_that_needs_a_login_says_how_to_give_one()
    {
        _probe.Throw = new AuthenticationRequiredException("needs login");

        var response = await Run(new CommandRequest { Verb = CommandVerb.Add, Urls = { "https://x.example.com/private.zip" } });

        Assert.False(response.Ok);
        Assert.Contains("user:password@", response.Error);
    }

    [Fact]
    public async Task A_checksum_QuickByte_cannot_read_refuses_the_whole_add()
    {
        var response = await Run(new CommandRequest { Verb = CommandVerb.Add, Urls = { "https://x.example.com/1.zip" }, Checksum = "not a hash" });

        Assert.False(response.Ok);
        Assert.Empty(_added);
    }

    [Theory]
    [InlineData(null, "1080p · 5 Mbps")]
    [InlineData("best", "1080p · 5 Mbps")]
    [InlineData("worst", "360p · 800 kbps")]
    [InlineData("720", "720p · 2.5 Mbps")]
    [InlineData("audio", "Audio only · 128 kbps")]
    public async Task A_streaming_video_is_added_in_the_quality_asked_for(string? quality, string expected)
    {
        _probe.Info = new RemoteFileInfo { FileName = "master.m3u8", ContentType = "application/vnd.apple.mpegurl", FinalUrl = "https://cdn.example.com/talks/keynote/master.m3u8" };

        var response = await Run(new CommandRequest { Verb = CommandVerb.Add, Urls = { "https://cdn.example.com/talks/keynote/master.m3u8" }, Quality = quality });

        Assert.True(response.Ok, response.Error);
        var request = Assert.Single(_added);
        Assert.Equal(expected, request.Stream!.Quality);
        Assert.StartsWith("keynote.", request.FileName);
    }

    [Fact]
    public async Task A_quality_the_video_does_not_have_is_refused_with_the_ones_it_does()
    {
        _probe.Info = new RemoteFileInfo { FileName = "master.m3u8", FinalUrl = "https://cdn.example.com/v/master.m3u8" };

        var response = await Run(new CommandRequest { Verb = CommandVerb.Add, Urls = { "https://cdn.example.com/v/master.m3u8" }, Quality = "4K" });

        Assert.False(response.Ok);
        Assert.Contains("720p", response.Error);
    }

    [Fact]
    public async Task A_download_is_found_by_the_start_of_its_id_and_only_when_that_is_unambiguous()
    {
        var first = _downloads.Add(new DownloadItem { Id = Guid.Parse("aaaa1111-0000-0000-0000-000000000000"), FileName = "one.zip", Status = DownloadStatus.Downloading });
        _downloads.Add(new DownloadItem { Id = Guid.Parse("aaaa2222-0000-0000-0000-000000000000"), FileName = "two.zip", Status = DownloadStatus.Downloading });

        Assert.Contains("more than one", (await Run(new CommandRequest { Verb = CommandVerb.Pause, Target = "aaaa" })).Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("at least", (await Run(new CommandRequest { Verb = CommandVerb.Pause, Target = "aa" })).Error);
        Assert.Contains("No download", (await Run(new CommandRequest { Verb = CommandVerb.Pause, Target = "bbbb" })).Error);

        var paused = await Run(new CommandRequest { Verb = CommandVerb.Pause, Target = "AAAA1" });
        Assert.True(paused.Ok);
        Assert.Equal(new[] { first.Id }, _downloads.Paused);
    }

    [Fact]
    public async Task Pausing_or_resuming_what_is_not_in_that_state_says_so()
    {
        _downloads.Add(new DownloadItem { Id = Guid.Parse("cccc0000-0000-0000-0000-000000000000"), FileName = "done.zip", Status = DownloadStatus.Completed });
        _downloads.Add(new DownloadItem { Id = Guid.Parse("dddd0000-0000-0000-0000-000000000000"), FileName = "bad.zip", Status = DownloadStatus.Failed });

        Assert.Contains("nothing to pause", (await Run(new CommandRequest { Verb = CommandVerb.Pause, Target = "cccc" })).Error);
        Assert.Contains("nothing to resume", (await Run(new CommandRequest { Verb = CommandVerb.Resume, Target = "cccc" })).Error);

        // A failed download resumes, as its Resume button does in the window.
        Assert.True((await Run(new CommandRequest { Verb = CommandVerb.Resume, Target = "dddd" })).Ok);
    }

    [Fact]
    public async Task Resume_all_resumes_only_what_can_be()
    {
        var paused = _downloads.Add(new DownloadItem { Status = DownloadStatus.Paused });
        _downloads.Add(new DownloadItem { Status = DownloadStatus.Completed });
        _downloads.Add(new DownloadItem { Status = DownloadStatus.Downloading });

        var response = await Run(new CommandRequest { Verb = CommandVerb.Resume, Target = "all" });

        Assert.Equal(new[] { paused.Id }, _downloads.Started);
        Assert.Equal("Resumed 1 download.", response.Message);
    }

    [Fact]
    public async Task A_queue_is_started_and_stopped_by_its_name_or_the_start_of_it()
    {
        _queues.Create("Nightly films");
        _queues.Create("Work");

        var started = await Run(new CommandRequest { Verb = CommandVerb.QueueStart, Queue = "nightly" });
        Assert.True(started.Ok, started.Error);
        Assert.Equal("Started queue \"Nightly films\".", started.Message);

        var missing = await Run(new CommandRequest { Verb = CommandVerb.QueueStop, Queue = "Weekend" });
        Assert.False(missing.Ok);
        Assert.Contains("no queue called", missing.Error);
    }

    [Fact]
    public async Task The_list_filters_by_status_and_the_status_counts_everything()
    {
        _downloads.Add(new DownloadItem { FileName = "a", Status = DownloadStatus.Downloading, CurrentSpeedBytesPerSecond = 1000 });
        _downloads.Add(new DownloadItem { FileName = "b", Status = DownloadStatus.Paused });
        _downloads.Add(new DownloadItem { FileName = "c", Status = DownloadStatus.Failed, ErrorMessage = "404 (Not Found)" });
        _downloads.Add(new DownloadItem { FileName = "d", Status = DownloadStatus.Completed });

        var failed = await Run(new CommandRequest { Verb = CommandVerb.List, Filter = "failed" });
        Assert.Equal("404 (Not Found)", Assert.Single(failed.Downloads!).Error);

        var status = (await Run(new CommandRequest { Verb = CommandVerb.Status })).Status!;
        Assert.Equal((4, 1, 1, 1, 1, 1000.0, "1.22.0"), (status.Downloads, status.Active, status.Paused, status.Failed, status.Completed, status.BytesPerSecond, status.Version));
    }

    // ------------------------------------------------------------- fakes --

    private sealed class FakeProbe : IRemoteFileInfoProvider
    {
        public RemoteFileInfo Info { get; set; } = new() { FileName = "file.zip", SupportsRangeRequests = true };
        public Exception? Throw { get; set; }
        public RequestOptions? LastOptions { get; private set; }

        public Task<RemoteFileInfo> GetFileInfoAsync(string url, RequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            LastOptions = options;
            if (Throw is not null) throw Throw;
            return Task.FromResult(new RemoteFileInfo
            {
                FileName = Info.FileName, ContentType = Info.ContentType, SupportsRangeRequests = Info.SupportsRangeRequests,
                ContentLength = Info.ContentLength, FinalUrl = string.IsNullOrEmpty(Info.FinalUrl) ? url : Info.FinalUrl
            });
        }
    }

    private sealed class FakeStreams : IStreamResolver
    {
        public Task<StreamDescription> DescribeAsync(string url, RequestOptions? options, CancellationToken cancellationToken)
        {
            StreamChoice Choice(string label, string ext) =>
                new(label, 1, new StreamSource { Format = StreamFormat.Hls, MainPlaylistUrl = url, Quality = label }, ext, "video/mp4");
            return Task.FromResult(new StreamDescription(StreamFormat.Hls, new[]
            {
                Choice("1080p · 5 Mbps", ".mp4"), Choice("720p · 2.5 Mbps", ".mp4"), Choice("360p · 800 kbps", ".mp4"), Choice("Audio only · 128 kbps", ".m4a")
            }));
        }
    }

    private sealed class FakeSettings : ISettingsService
    {
        public FakeSettings(DownloadSettings settings) => Current = settings;
        public DownloadSettings Current { get; }
#pragma warning disable CS0067
        public event EventHandler<DownloadSettings>? SettingsChanged;
#pragma warning restore CS0067
        public void Load() { }
        public void Save(DownloadSettings settings) { }
    }

    private sealed class MemoryQueues : IQueueRepository
    {
        public List<DownloadQueue> LoadAll() => new();
        public bool TryLoadAll(out List<DownloadQueue> queues) { queues = new(); return true; }
        public void SaveAll(IEnumerable<DownloadQueue> queues) { }
    }

    private sealed class InlineDispatcher : IDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class FakeDownloads : IDownloadManager
    {
        private readonly List<DownloadItem> _items = new();

        public List<Guid> Started { get; } = new();
        public List<Guid> Paused { get; } = new();

        public DownloadItem Add(DownloadItem item)
        {
            _items.Add(item);
            return item;
        }

        public IReadOnlyList<DownloadItem> Downloads => _items.ToList();

#pragma warning disable CS0067
        public event EventHandler<DownloadListChangedEventArgs>? DownloadListChanged;
        public event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
        public event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;
        public event EventHandler<ConnectionsSnapshotEventArgs>? ConnectionsChanged;
#pragma warning restore CS0067

        public Task StartAsync(Guid downloadId) { Started.Add(downloadId); return Task.CompletedTask; }
        public Task ResumeAsync(Guid downloadId) => StartAsync(downloadId);
        public void Pause(Guid downloadId) => Paused.Add(downloadId);
        public int PauseAll() => _items.Count(DownloadActions.CanPause);

        public Task<DownloadItem> AddDownloadAsync(DownloadRequest request) => throw new NotSupportedException();
        public IDownloadService? GetService(Guid downloadId) => null;
        public void Stop(Guid downloadId) { }
        public Task RetryAsync(Guid downloadId) => Task.CompletedTask;
        public void Remove(Guid downloadId, bool deleteFile) { }
        public bool ChangeAddress(Guid downloadId, AddressChange change) => false;
        public Task<ChecksumResult> VerifyChecksumAsync(Guid downloadId, ExpectedChecksum expected, IProgress<long>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(ChecksumResult.NotChecked);
        public long GlobalSpeedLimitBytesPerSecond => 0;
        public void SetSpeedLimit(Guid downloadId, long bytesPerSecond) { }
        public void SetCompletionActions(Guid downloadId, CompletionActions actions, PowerAction power) { }
        public void SetQueueSpeedLimit(Guid downloadId, long bytesPerSecond) { }
        public void SetGlobalSpeedLimit(long bytesPerSecond) { }
        public void LoadPersistedDownloads() { }
        public Task<int> CleanupOrphanedTempFoldersAsync() => Task.FromResult(0);
    }
}

/// <summary>What qb prints — a script that reads it depends on its shape.</summary>
public sealed class CommandFormatterTests
{
    public CommandFormatterTests() => CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

    [Fact]
    public void Downloads_are_a_table_with_the_short_id_qb_pause_accepts()
    {
        var response = new CommandResponse
        {
            Ok = true,
            Downloads = new()
            {
                new DownloadSummary { Id = "3f9c0e1a2b4d6f80a1c3e5f7a9b1c3d5", FileName = "ubuntu.iso", Status = "downloading", TotalBytes = 1024 * 1024, Percent = 42.5, BytesPerSecond = 2048, Queue = "Night" },
                new DownloadSummary { Id = "0123456789abcdef0123456789abcdef", FileName = "x.bin", Status = "failed", Error = "404 (Not Found)" }
            }
        };

        string[] lines = CommandFormatter.Format(new CommandRequest { Verb = CommandVerb.List }, response, json: false).Split(Environment.NewLine);

        Assert.StartsWith("ID        STATUS       DONE   SIZE     SPEED      NAME", lines[0]);
        Assert.StartsWith("3f9c0e1a  downloading  42.5%  1.00 MB  2.00 KB/s  ubuntu.iso  [Night]", lines[1]);
        Assert.EndsWith("x.bin  - 404 (Not Found)", lines[2]);
    }

    [Fact]
    public void Json_is_the_response_itself_in_camel_case_without_empty_parts()
    {
        var response = new CommandResponse { Ok = true, Queues = new() { new QueueSummary { Id = "q", Name = "Night", State = "idle", Files = 3 } } };

        string json = CommandFormatter.Format(new CommandRequest { Verb = CommandVerb.Queues }, response, json: true);
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("Night", document.RootElement.GetProperty("queues")[0].GetProperty("name").GetString());
        Assert.False(document.RootElement.TryGetProperty("downloads", out _));
        Assert.False(document.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public void An_empty_list_says_so_rather_than_printing_nothing()
    {
        Assert.Equal("No downloads.", CommandFormatter.Format(new CommandRequest { Verb = CommandVerb.List }, new CommandResponse { Ok = true, Downloads = new() }, false));
        Assert.Equal("No queues.", CommandFormatter.Format(new CommandRequest { Verb = CommandVerb.Queues }, new CommandResponse { Ok = true, Queues = new() }, false));
    }
}

/// <summary>The pipe between qb and QuickByte: a request answered, nobody there, and a request that is not one.</summary>
public sealed class CommandPipeTests
{
    private static string UniqueName() => $"QuickByte.Command.Test.{Guid.NewGuid():N}";

    [Fact]
    public async Task A_request_is_answered_over_the_pipe()
    {
        string name = UniqueName();
        using var stop = new CancellationTokenSource();
        CommandRequest? received = null;
        var serving = CommandPipe.ServeAsync(request =>
        {
            received = request;
            return Task.FromResult(new CommandResponse { Ok = true, Message = $"got {request.Urls.Count}" });
        }, stop.Token, name);

        var response = await CommandPipe.SendAsync(
            new CommandRequest { Verb = CommandVerb.Add, Urls = { "https://a", "https://b" }, Folder = @"D:\Ünïcode" },
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), name);

        Assert.Equal("got 2", response!.Message);
        Assert.Equal(@"D:\Ünïcode", received!.Folder);

        // And the next client is answered too.
        Assert.NotNull(await CommandPipe.SendAsync(new CommandRequest { Verb = CommandVerb.Status }, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), name));

        stop.Cancel();
        await serving;
    }

    [Fact]
    public async Task Nobody_listening_is_an_answer_of_null_not_a_hang()
    {
        var started = DateTime.UtcNow;

        var response = await CommandPipe.SendAsync(new CommandRequest(), TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5), UniqueName());

        Assert.Null(response);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task A_request_that_is_not_one_is_answered_with_an_error()
    {
        string name = UniqueName();
        using var stop = new CancellationTokenSource();
        var serving = CommandPipe.ServeAsync(_ => throw new InvalidOperationException("must not be called"), stop.Token, name);

        using (var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.CurrentUserOnly))
        {
            await client.ConnectAsync(5000);
            await client.WriteAsync(Encoding.UTF8.GetBytes("{ not json\n"));
            using var reader = new StreamReader(client);
            string reply = (await reader.ReadLineAsync())!;

            Assert.Contains("did not understand", JsonSerializer.Deserialize<CommandResponse>(reply)!.Error);
        }

        stop.Cancel();
        await serving;
    }
}
