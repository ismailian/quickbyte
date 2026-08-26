using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.Core.Services;
using QuickByte.Core.Services.Ftp;

namespace QuickByte.Core.Tests.Services;

/// <summary>
/// The two dispatchers are the only things in the engine that look at a URL's
/// scheme, and they must stay in step: a file probed over one protocol and
/// fetched over the other resolves one size and downloads something else.
/// </summary>
public sealed class ProtocolDispatchTests
{
    public static TheoryData<string, bool> Urls => new()
    {
        { "ftp://example.com/disk.iso", true },
        { "FTP://example.com/disk.iso", true },
        { "ftps://example.com/disk.iso", true },
        { "FTPS://example.com/disk.iso", true },
        { "http://example.com/file.bin", false },
        { "https://example.com/file.bin", false },
        { "HTTPS://example.com/file.bin", false }
    };

    [Theory]
    [MemberData(nameof(Urls))]
    public void The_connection_factory_routes_by_scheme(string url, bool expectFtp)
    {
        var http = new RecordingConnectionFactory();
        var ftp = new RecordingConnectionFactory();

        new ProtocolConnectionFactory(http, ftp)
            .Create(0, url, 0, 99, 0, "chunk.tmp", new DownloadSettings());

        Assert.Equal(expectFtp, ftp.Called);
        Assert.Equal(!expectFtp, http.Called);
    }

    [Theory]
    [MemberData(nameof(Urls))]
    public async Task The_file_info_provider_routes_by_the_same_rule(string url, bool expectFtp)
    {
        var http = new RecordingInfoProvider();
        var ftp = new RecordingInfoProvider();

        await new ProtocolFileInfoProvider(http, ftp).GetFileInfoAsync(url);

        Assert.Equal(expectFtp, ftp.Called);
        Assert.Equal(!expectFtp, http.Called);
    }

    [Theory]
    [MemberData(nameof(Urls))]
    public async Task Both_dispatchers_agree_about_every_url(string url, bool expectFtp)
    {
        // The invariant stated as one assertion: whichever protocol answers the
        // probe is the one that fetches the bytes.
        var probeHttp = new RecordingInfoProvider();
        var probeFtp = new RecordingInfoProvider();
        var fetchHttp = new RecordingConnectionFactory();
        var fetchFtp = new RecordingConnectionFactory();

        await new ProtocolFileInfoProvider(probeHttp, probeFtp).GetFileInfoAsync(url);
        new ProtocolConnectionFactory(fetchHttp, fetchFtp)
            .Create(0, url, 0, 99, 0, "chunk.tmp", new DownloadSettings());

        Assert.Equal(probeFtp.Called, fetchFtp.Called);
        Assert.Equal(probeHttp.Called, fetchHttp.Called);
        Assert.Equal(expectFtp, probeFtp.Called);
    }

    [Fact]
    public void Something_that_is_not_a_url_falls_to_http()
    {
        // FtpUrl.IsFtp cannot parse it, so it is not FTP — and HTTP will produce
        // the error the user needs to see.
        var http = new RecordingConnectionFactory();
        var ftp = new RecordingConnectionFactory();

        new ProtocolConnectionFactory(http, ftp)
            .Create(0, "not a url", 0, 99, 0, "chunk.tmp", new DownloadSettings());

        Assert.True(http.Called);
        Assert.False(ftp.Called);
    }

    [Fact]
    public void The_dispatcher_passes_every_argument_through_untouched()
    {
        var ftp = new RecordingConnectionFactory();
        var options = new RequestOptions { Credentials = new DownloadCredentials { UserName = "alice" } };

        new ProtocolConnectionFactory(new RecordingConnectionFactory(), ftp)
            .Create(3, "ftp://example.com/x.iso", 100, 199, 40, "part3.tmp", new DownloadSettings(), null, options);

        Assert.Equal(3, ftp.ConnectionId);
        Assert.Equal(100, ftp.RangeStart);
        Assert.Equal(199, ftp.RangeEnd);
        Assert.Equal(40, ftp.AlreadyDownloaded);
        Assert.Equal("part3.tmp", ftp.ChunkFilePath);
        Assert.Same(options, ftp.Options);
    }

    private sealed class RecordingConnectionFactory : IConnectionFactory
    {
        public bool Called { get; private set; }
        public int ConnectionId { get; private set; }
        public long RangeStart { get; private set; }
        public long RangeEnd { get; private set; }
        public long AlreadyDownloaded { get; private set; }
        public string? ChunkFilePath { get; private set; }
        public RequestOptions? Options { get; private set; }

        public IDownloadConnection Create(
            int connectionId, string url, long rangeStart, long rangeEnd, long alreadyDownloaded,
            string chunkFilePath, DownloadSettings settings,
            IBandwidthLimiter? bandwidthLimiter = null, RequestOptions? options = null)
        {
            Called = true;
            ConnectionId = connectionId;
            RangeStart = rangeStart;
            RangeEnd = rangeEnd;
            AlreadyDownloaded = alreadyDownloaded;
            ChunkFilePath = chunkFilePath;
            Options = options;
            return null!;
        }
    }

    private sealed class RecordingInfoProvider : IRemoteFileInfoProvider
    {
        public bool Called { get; private set; }

        public Task<RemoteFileInfo> GetFileInfoAsync(
            string url, RequestOptions? options = null, CancellationToken cancellationToken = default)
        {
            Called = true;
            return Task.FromResult(new RemoteFileInfo());
        }
    }
}

/// <summary>
/// The two strings the FTP protocol actually wants out of a URL.
/// </summary>
public sealed class FtpUrlTests
{
    [Theory]
    [InlineData("ftp://example.com/x.iso", true)]
    [InlineData("ftps://example.com/x.iso", true)]
    [InlineData("FtP://example.com/x.iso", true)]
    [InlineData("https://example.com/x.iso", false)]
    [InlineData("sftp://example.com/x.iso", false)]
    [InlineData("nonsense", false)]
    [InlineData("", false)]
    public void IsFtp_recognises_only_the_schemes_the_ftp_client_handles(string url, bool expected) =>
        Assert.Equal(expected, FtpUrl.IsFtp(url));

    [Fact]
    public void PathOf_undoes_percent_encoding_before_the_path_goes_on_the_wire()
    {
        // FTP has no such encoding, so a %20 left in place makes the server look
        // for a file whose name really does contain a percent sign.
        Assert.Equal("/pub/my file.iso", FtpUrl.PathOf(new Uri("ftp://example.com/pub/my%20file.iso")));
    }

    [Fact]
    public void PathOf_a_bare_host_is_the_root() =>
        Assert.Equal("/", FtpUrl.PathOf(new Uri("ftp://example.com")));

    [Fact]
    public void FileNameOf_takes_the_last_segment() =>
        Assert.Equal("disk.iso", FtpUrl.FileNameOf(new Uri("ftp://example.com/pub/disk.iso")));

    [Fact]
    public void FileNameOf_sanitizes_what_it_finds() =>
        Assert.Equal("a_b.iso", FtpUrl.FileNameOf(new Uri("ftp://example.com/pub/a%3Ab.iso")));

    [Fact]
    public void FileNameOf_falls_back_when_the_url_names_no_file() =>
        Assert.Equal("download", FtpUrl.FileNameOf(new Uri("ftp://example.com/pub/")));
}

/// <summary>
/// The first digit of an FTP reply is the whole protocol's error model, which is
/// why the reply is read by range rather than by matching the specific numbers
/// each command happens to return.
/// </summary>
public sealed class FtpReplyTests
{
    [Theory]
    [InlineData(200, true)]
    [InlineData(226, true)]
    [InlineData(350, true)]
    [InlineData(399, true)]
    [InlineData(150, false)]
    [InlineData(400, false)]
    [InlineData(550, false)]
    public void IsPositive_covers_2xx_and_3xx(int code, bool expected) =>
        Assert.Equal(expected, new FtpReply(code, "text").IsPositive);

    [Theory]
    [InlineData(125, true)]
    [InlineData(150, true)]
    [InlineData(200, false)]
    [InlineData(99, false)]
    public void IsPreliminary_is_how_a_transfer_is_known_to_have_started(int code, bool expected) =>
        Assert.Equal(expected, new FtpReply(code, "text").IsPreliminary);

    [Theory]
    [InlineData(530, true)]
    [InlineData(532, true)]
    [InlineData(531, false)]
    [InlineData(550, false)]
    public void IsAuthenticationFailure_names_the_two_login_refusals(int code, bool expected) =>
        Assert.Equal(expected, new FtpReply(code, "text").IsAuthenticationFailure);

    [Fact]
    public void A_reply_prints_as_its_text() =>
        Assert.Equal("213 1048576", new FtpReply(213, "213 1048576").ToString());
}
