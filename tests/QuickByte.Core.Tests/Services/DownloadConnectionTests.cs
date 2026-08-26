using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using QuickByte.Core.Enums;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;
using QuickByte.Core.Models;
using QuickByte.Core.Services;

namespace QuickByte.Core.Tests.Services;

/// <summary>
/// One connection against a stubbed server.
///
/// Most of what is checked here is what happens when the server does <em>not</em>
/// do as it was asked. A response that ignores the Range header, or stops short
/// of the range it promised, is not an error anywhere in HTTP — the bytes simply
/// land at the wrong offset in a chunk file, the pool sees a connection that ran
/// to the end of its loop, and the merge writes the damage into the finished
/// file. Nothing downstream can tell, so it has to be caught here.
/// </summary>
public sealed class DownloadConnectionTests
{
    private static readonly byte[] File100 = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();

    private static DownloadSettings Settings(int maxRetries = 0) =>
        new() { MaxRetries = maxRetries, RetryDelayMilliseconds = 0 };

    // ------------------------------------------------------- the happy path --

    [Fact]
    public async Task A_segment_the_server_honours_lands_in_its_chunk()
    {
        using var folder = new TempFolder();
        using var stub = new StubServer(_ => Partial(File100[50..], 50, 99, 100));
        var connection = Connection(stub, folder.File("part1.tmp"), rangeStart: 50, rangeEnd: 99);

        await connection.RunAsync(CancellationToken.None);

        Assert.Equal(File100[50..], File.ReadAllBytes(folder.File("part1.tmp")));
        Assert.Equal(50, connection.BytesDownloaded);
        Assert.Equal(ConnectionStatus.Finished, connection.Status);
    }

    [Fact]
    public async Task A_resumed_segment_asks_for_what_is_missing_and_appends_it()
    {
        using var folder = new TempFolder();
        folder.WriteFile("part0.tmp", File100[..40]);
        using var stub = new StubServer(_ => Partial(File100[40..], 40, 99, 100));

        var connection = Connection(stub, folder.File("part0.tmp"), 0, 99, alreadyDownloaded: 40);
        await connection.RunAsync(CancellationToken.None);

        Assert.Equal("bytes=40-99", stub.Ranges.Single());
        Assert.Equal(File100, File.ReadAllBytes(folder.File("part0.tmp")));
    }

    [Fact]
    public async Task A_segment_already_complete_on_disk_sends_no_request()
    {
        using var folder = new TempFolder();
        folder.WriteFile("part0.tmp", File100);
        using var stub = new StubServer(_ => throw new InvalidOperationException("should not be asked"));

        var connection = Connection(stub, folder.File("part0.tmp"), 0, 99, alreadyDownloaded: 100);
        await connection.RunAsync(CancellationToken.None);

        Assert.Empty(stub.Ranges);
        Assert.Equal(ConnectionStatus.Finished, connection.Status);
    }

    // ------------------------------------ a server that ignores the range --

    [Fact]
    public async Task A_resumed_first_segment_answered_with_the_whole_file_starts_again_from_zero()
    {
        // The regression this exists for: a 200 means the body begins at byte 0,
        // and writing it at the resume offset appends the whole file to the
        // partial. The chunk used to come back 140 bytes long with its first 40
        // duplicated, and nothing downstream would have noticed.
        using var folder = new TempFolder();
        folder.WriteFile("part0.tmp", File100[..40]);
        using var stub = new StubServer(_ => Whole(File100));

        var connection = Connection(stub, folder.File("part0.tmp"), 0, 99, alreadyDownloaded: 40);
        await connection.RunAsync(CancellationToken.None);

        Assert.Equal(File100, File.ReadAllBytes(folder.File("part0.tmp")));
        Assert.Equal(100, connection.BytesDownloaded);
        Assert.Equal(ConnectionStatus.Finished, connection.Status);
    }

    [Fact]
    public async Task A_restart_from_zero_leaves_no_tail_of_the_longer_chunk_it_replaced()
    {
        // A file of unknown length that shrank between the pause and the resume,
        // on a server that then ignores the Range header. Without truncating, the
        // chunk keeps the last 30 bytes of the previous, longer version past the
        // end of the new content — and with no known length there is no
        // short-transfer check to catch it.
        using var folder = new TempFolder();
        folder.WriteFile("part0.tmp", Enumerable.Repeat((byte)0xEE, 90).ToArray());
        using var stub = new StubServer(_ => Whole(File100[..60]));

        var connection = Connection(
            stub, folder.File("part0.tmp"), 0, RangeSplitter.UnboundedEnd, alreadyDownloaded: 90);
        await connection.RunAsync(CancellationToken.None);

        Assert.Equal(File100[..60], File.ReadAllBytes(folder.File("part0.tmp")));
    }

    [Fact]
    public async Task A_chunk_longer_than_its_range_is_read_as_a_finished_segment()
    {
        // Not a bug in the connection — the resume offset is clamped to the size
        // of the range, so there is nothing left for it to fetch. It is the
        // reason ConnectionPoolManager has to throw away a chunk set from a
        // different split *before* it builds connections over it: on its own,
        // this connection would report Finished and hand the merge 100 bytes
        // where the range covers 50.
        using var folder = new TempFolder();
        folder.WriteFile("part0.tmp", File100);
        using var stub = new StubServer(_ => throw new InvalidOperationException("should not be asked"));

        var connection = Connection(stub, folder.File("part0.tmp"), 0, 49, alreadyDownloaded: 100);
        await connection.RunAsync(CancellationToken.None);

        Assert.Empty(stub.Ranges);
        Assert.Equal(ConnectionStatus.Finished, connection.Status);
    }

    [Fact]
    public async Task A_later_segment_answered_with_the_whole_file_fails_instead_of_writing_it()
    {
        // Segment 1 of 2 cannot be salvaged from a body that starts at byte 0.
        // Before this check the connection wrote the file's first 50 bytes into
        // the chunk that owns bytes 50-99 and reported itself Finished.
        using var folder = new TempFolder();
        using var stub = new StubServer(_ => Whole(File100));
        var connection = Connection(stub, folder.File("part1.tmp"), rangeStart: 50, rangeEnd: 99);

        await Assert.ThrowsAsync<IOException>(() => connection.RunAsync(CancellationToken.None));

        Assert.NotEqual(ConnectionStatus.Finished, connection.Status);
        Assert.False(File.Exists(folder.File("part1.tmp")));
    }

    [Fact]
    public async Task A_partial_response_that_starts_somewhere_else_is_refused()
    {
        // The same failure wearing the right status code.
        using var folder = new TempFolder();
        using var stub = new StubServer(_ => Partial(File100[..50], 0, 49, 100));
        var connection = Connection(stub, folder.File("part1.tmp"), rangeStart: 50, rangeEnd: 99);

        var thrown = await Assert.ThrowsAsync<IOException>(() => connection.RunAsync(CancellationToken.None));

        Assert.Contains("50", thrown.Message);
    }

    // --------------------------------------- a server that sends too much --

    [Fact]
    public async Task A_response_longer_than_the_segment_is_cut_off_at_its_end()
    {
        // Otherwise this chunk runs on over its neighbour's bytes, and the merge
        // produces a file longer than the download.
        using var folder = new TempFolder();
        using var stub = new StubServer(_ => Partial(File100, 0, 49, 100));
        var connection = Connection(stub, folder.File("part0.tmp"), rangeStart: 0, rangeEnd: 49);

        await connection.RunAsync(CancellationToken.None);

        Assert.Equal(File100[..50], File.ReadAllBytes(folder.File("part0.tmp")));
        Assert.Equal(50, connection.BytesDownloaded);
    }

    // -------------------------------------- a server that stops too early --

    [Fact]
    public async Task A_transfer_that_ends_early_is_reported_rather_than_accepted()
    {
        // A proxy closing a connection cleanly reads as end-of-stream. The
        // connection used to mark itself Finished with a short chunk, and the
        // merge wrote the hole into the final file.
        using var folder = new TempFolder();
        using var stub = new StubServer(_ => Partial(File100[..60], 0, 99, 100));
        var connection = Connection(stub, folder.File("part0.tmp"), 0, 99);

        var thrown = await Assert.ThrowsAsync<IOException>(() => connection.RunAsync(CancellationToken.None));

        Assert.Contains("40 bytes early", thrown.Message);
        Assert.NotEqual(ConnectionStatus.Finished, connection.Status);
    }

    [Fact]
    public async Task A_retry_after_an_early_end_carries_on_from_the_bytes_that_arrived()
    {
        using var folder = new TempFolder();
        int attempt = 0;
        using var stub = new StubServer(_ =>
            attempt++ == 0
                ? Partial(File100[..60], 0, 99, 100)
                : Partial(File100[60..], 60, 99, 100));

        var connection = Connection(stub, folder.File("part0.tmp"), 0, 99, settings: Settings(maxRetries: 3));
        await connection.RunAsync(CancellationToken.None);

        Assert.Equal(new[] { "bytes=0-99", "bytes=60-99" }, stub.Ranges);
        Assert.Equal(File100, File.ReadAllBytes(folder.File("part0.tmp")));
        Assert.Equal(ConnectionStatus.Finished, connection.Status);
        Assert.Equal(1, connection.RetryCount);
    }

    // --------------------------------------------------- an unknown length --

    [Fact]
    public async Task A_segment_with_no_known_end_asks_for_an_open_ended_range()
    {
        // Spelling the sentinel out as 9223372036854775806 is a range a fair
        // number of servers answer with 416 rather than the rest of the file.
        using var folder = new TempFolder();
        using var stub = new StubServer(_ => Whole(File100));

        var connection = Connection(stub, folder.File("part0.tmp"), 0, RangeSplitter.UnboundedEnd);
        await connection.RunAsync(CancellationToken.None);

        Assert.Equal("bytes=0-", stub.Ranges.Single());
        Assert.Equal(File100, File.ReadAllBytes(folder.File("part0.tmp")));
        Assert.Equal(ConnectionStatus.Finished, connection.Status);
    }

    [Fact]
    public async Task A_stream_ending_is_the_end_of_a_file_whose_length_was_never_known()
    {
        using var folder = new TempFolder();
        using var stub = new StubServer(_ => Whole(File100));

        var connection = Connection(stub, folder.File("part0.tmp"), 0, RangeSplitter.UnboundedEnd);
        await connection.RunAsync(CancellationToken.None);

        // No "ended early" — there was nothing to end early against.
        Assert.Equal(ConnectionStatus.Finished, connection.Status);
    }

    // --------------------------------------------------------- challenges --

    [Fact]
    public async Task A_challenge_is_raised_as_the_one_error_the_user_can_act_on()
    {
        using var folder = new TempFolder();
        using var stub = new StubServer(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var connection = Connection(stub, folder.File("part0.tmp"), 0, 99, settings: Settings(maxRetries: 3));
        var thrown = await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => connection.RunAsync(CancellationToken.None));

        Assert.False(thrown.CredentialsWereSupplied);

        // And it is not retried: a wrong password is just as wrong on the fourth
        // attempt, and some servers count the repeats towards a lockout.
        Assert.Single(stub.Ranges);
    }

    [Fact]
    public async Task A_rejected_password_says_so()
    {
        using var folder = new TempFolder();
        using var stub = new StubServer(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var connection = Connection(stub, folder.File("part0.tmp"), 0, 99, options: new RequestOptions
        {
            Credentials = new DownloadCredentials { UserName = "alice", Password = "wrong" }
        });

        var thrown = await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => connection.RunAsync(CancellationToken.None));

        Assert.True(thrown.CredentialsWereSupplied);
    }

    [Fact]
    public async Task An_ordinary_failure_is_retried_and_then_surfaced()
    {
        using var folder = new TempFolder();
        using var stub = new StubServer(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var connection = Connection(stub, folder.File("part0.tmp"), 0, 99, settings: Settings(maxRetries: 2));
        await Assert.ThrowsAsync<HttpRequestException>(() => connection.RunAsync(CancellationToken.None));

        Assert.Equal(3, stub.Ranges.Count);
        Assert.Equal(2, connection.RetryCount);
        Assert.NotNull(connection.LastError);
    }

    // ------------------------------------------------------------- plumbing --

    private static DownloadConnection Connection(
        StubServer stub,
        string chunkPath,
        long rangeStart,
        long rangeEnd,
        long alreadyDownloaded = 0,
        DownloadSettings? settings = null,
        RequestOptions? options = null) =>
        new(stub.Client, connectionId: 0, "https://example.com/file.bin",
            rangeStart, rangeEnd, alreadyDownloaded, chunkPath, settings ?? Settings(), null, options);

    private static HttpResponseMessage Partial(byte[] body, long from, long to, long total)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        {
            Content = new ByteArrayContent(body)
        };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, total);
        return response;
    }

    private static HttpResponseMessage Whole(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    /// <summary>An <see cref="HttpClient"/> whose answers the test writes, recording what was asked.</summary>
    private sealed class StubServer : IDisposable
    {
        private readonly Handler _handler;

        public StubServer(Func<HttpRequestMessage, HttpResponseMessage> respond)
        {
            _handler = new Handler(respond);
            Client = new HttpClient(_handler);
        }

        public HttpClient Client { get; }

        /// <summary>The Range header of each request, in order.</summary>
        public List<string> Ranges => _handler.Ranges;

        public void Dispose() => Client.Dispose();

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

            public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

            public List<string> Ranges { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                // Captured now: the connection disposes the request as soon as the
                // response comes back.
                Ranges.Add(request.Headers.Range?.ToString() ?? string.Empty);
                return Task.FromResult(_respond(request));
            }
        }
    }
}
