using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Models;
using QuickByte.Core.Services;

namespace QuickByte.Core.Tests.Services;

/// <summary>
/// The probe, against servers that answer badly.
///
/// One question decides whether a download is split across connections or runs
/// on one, and getting it wrong in the optimistic direction does not produce a
/// slow download — it produces a failed one. Seven of eight connections are
/// handed a response that starts at byte zero, refuse it (see
/// <see cref="DownloadConnectionTests"/>), and exhaust their retries.
///
/// The answers that cause that are not errors in HTTP and cannot be provoked
/// from a real server on demand, so each one is stubbed here:
///
/// <list type="bullet">
/// <item><description>
/// <c>Accept-Ranges: bytes</c> from something that then ignores a Range header
/// — a CDN edge answering from a whole copy it cannot slice, which is what
/// protonvpn.com's download endpoint does.
/// </description></item>
/// <item><description>
/// The same, but where asking past the cache <em>does</em> produce a 206, so
/// the file is segmentable after all provided every connection asks that way.
/// </description></item>
/// <item><description>
/// A server that never claimed ranges at all, which must not be pestered with a
/// second probe.
/// </description></item>
/// </list>
/// </summary>
public sealed class RemoteFileInfoProviderTests
{
    private const string Url = "https://example.com/setup.exe";
    private const long Size = 130_079_008;

    // ------------------------------------------------ range support is proven --

    [Fact]
    public async Task A_206_to_the_probe_is_what_makes_a_file_segmentable()
    {
        using var stub = new StubServer(Head(size: Size, acceptRanges: true), PartialProbe());

        var info = await new RemoteFileInfoProvider(stub.Client).GetFileInfoAsync(Url);

        Assert.True(info.SupportsRangeRequests);
        Assert.False(info.BypassCache);
        Assert.Equal(Size, info.ContentLength);
    }

    [Fact]
    public async Task A_head_that_answered_everything_is_still_made_to_prove_the_range()
    {
        // The regression this file exists for. HEAD used to be allowed to settle
        // range support on its own whenever it also supplied a size and a name,
        // which meant the claim was never checked on exactly the servers that
        // answer HEAD most completely.
        using var stub = new StubServer(
            Head(size: Size, acceptRanges: true, fileName: "setup.exe"),
            WholeFileProbe(),   // ignores the range
            WholeFileProbe());  // ...and still ignores it past the cache

        var info = await new RemoteFileInfoProvider(stub.Client).GetFileInfoAsync(Url);

        Assert.False(info.SupportsRangeRequests);
        Assert.True(info.ServerClaimsRangeSupport);

        // Named and sized as before — only the claim about ranges was refused.
        Assert.Equal("setup.exe", info.FileName);
        Assert.Equal(Size, info.ContentLength);
    }

    // --------------------------------------- a cache that cannot slice a file --

    [Fact]
    public async Task A_cache_that_answers_a_range_with_the_whole_file_is_asked_past()
    {
        using var stub = new StubServer(
            Head(size: Size, acceptRanges: true),
            WholeFileProbe(),
            PartialProbe());

        var info = await new RemoteFileInfoProvider(stub.Client).GetFileInfoAsync(Url);

        Assert.True(info.SupportsRangeRequests);

        // Recorded, because the connections have to ask the same way. Without
        // it every segment but the first goes back to the same cache and gets
        // the same whole-file answer the probe just worked around.
        Assert.True(info.BypassCache);
    }

    [Fact]
    public async Task The_second_probe_is_the_one_that_asks_the_origin()
    {
        using var stub = new StubServer(
            Head(size: Size, acceptRanges: true),
            WholeFileProbe(),
            PartialProbe());

        await new RemoteFileInfoProvider(stub.Client).GetFileInfoAsync(Url);

        Assert.Equal(3, stub.Requests.Count);
        Assert.Null(stub.Requests[1].CacheControl);
        Assert.Equal("no-cache", stub.Requests[2].CacheControl);

        // Pragma too: it is the HTTP/1.0 spelling and still the only one some
        // intermediaries act on.
        Assert.Equal("no-cache", stub.Requests[2].Pragma);
    }

    [Fact]
    public async Task A_server_that_never_claimed_ranges_is_not_asked_twice()
    {
        // No Accept-Ranges anywhere, and a 200 to the probe. There is no
        // contradiction to investigate here, just a server that does not do
        // ranges — and a second probe would be a request spent learning nothing.
        using var stub = new StubServer(
            Head(size: Size, acceptRanges: false), WholeFileProbe(acceptRanges: false));

        var info = await new RemoteFileInfoProvider(stub.Client).GetFileInfoAsync(Url);

        Assert.False(info.SupportsRangeRequests);
        Assert.False(info.BypassCache);
        Assert.Equal(2, stub.Requests.Count);
    }

    [Fact]
    public async Task A_download_that_already_knows_to_bypass_the_cache_probes_that_way_once()
    {
        // What a resume looks like: the item carries BypassCache from the run
        // that discovered it. The probe must ask that way from the start — and
        // must not then read its own success as "no bypass was needed" and strip
        // the headers off every connection.
        using var stub = new StubServer(Head(size: Size, acceptRanges: true), PartialProbe());

        var info = await new RemoteFileInfoProvider(stub.Client)
            .GetFileInfoAsync(Url, new RequestOptions { BypassCache = true });

        Assert.True(info.SupportsRangeRequests);
        Assert.True(info.BypassCache);
        Assert.Equal(2, stub.Requests.Count);
        Assert.All(stub.Requests, request => Assert.Equal("no-cache", request.CacheControl));
    }

    [Fact]
    public async Task A_bypassing_probe_still_refused_a_range_leaves_the_file_on_one_connection()
    {
        // The bypass is a hypothesis, not a guarantee. When it is wrong the
        // answer has to be "one connection", not a third attempt at the same
        // request — that is a loop.
        using var stub = new StubServer(
            Head(size: Size, acceptRanges: true), WholeFileProbe(), WholeFileProbe());

        var info = await new RemoteFileInfoProvider(stub.Client)
            .GetFileInfoAsync(Url, new RequestOptions { BypassCache = true });

        Assert.False(info.SupportsRangeRequests);
        Assert.Equal(2, stub.Requests.Count);
    }

    // ------------------------------------------------- when the probe cannot --

    [Fact]
    public async Task A_probe_that_fails_after_a_complete_head_keeps_what_the_head_resolved()
    {
        // HEAD answered the questions the Add dialog asks. Throwing that away
        // because a follow-up request could not run turns a download that works
        // on one connection into a link the user is told is broken.
        using var stub = new StubServer(
            Head(size: Size, acceptRanges: true, fileName: "setup.exe"),
            _ => throw new HttpRequestException("connection reset"));

        var info = await new RemoteFileInfoProvider(stub.Client).GetFileInfoAsync(Url);

        Assert.Equal("setup.exe", info.FileName);
        Assert.Equal(Size, info.ContentLength);
        Assert.False(info.SupportsRangeRequests);
    }

    [Fact]
    public async Task A_probe_that_fails_when_the_head_did_not_answer_is_reported()
    {
        using var stub = new StubServer(
            _ => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed),
            _ => throw new HttpRequestException("connection reset"));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => new RemoteFileInfoProvider(stub.Client).GetFileInfoAsync(Url));
    }

    [Fact]
    public async Task A_401_on_the_probe_is_a_credentials_prompt_rather_than_a_failure()
    {
        using var stub = new StubServer(
            _ => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var thrown = await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => new RemoteFileInfoProvider(stub.Client).GetFileInfoAsync(Url));

        Assert.False(thrown.CredentialsWereSupplied);
    }

    [Fact]
    public async Task A_401_survives_a_complete_head_rather_than_being_swallowed_as_a_probe_failure()
    {
        // The catch that rescues a failed probe must not rescue this one: a
        // download the user could sign in to would otherwise be added as an
        // anonymous single-connection fetch of a login page.
        using var stub = new StubServer(
            Head(size: Size, acceptRanges: true, fileName: "setup.exe"),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<AuthenticationRequiredException>(
            () => new RemoteFileInfoProvider(stub.Client).GetFileInfoAsync(Url));
    }

    // ---------------------------------------------------------- what it reads --

    [Fact]
    public async Task The_size_comes_from_content_range_not_from_the_one_byte_body()
    {
        // The probe asks for a single byte, so its own Content-Length is 1.
        using var stub = new StubServer(_ => new HttpResponseMessage(HttpStatusCode.MethodNotAllowed), PartialProbe());

        var info = await new RemoteFileInfoProvider(stub.Client).GetFileInfoAsync(Url);

        Assert.Equal(Size, info.ContentLength);
    }

    [Fact]
    public async Task A_name_the_probe_supplies_is_used_when_the_head_named_nothing()
    {
        using var stub = new StubServer(
            Head(size: Size, acceptRanges: true),
            request => Named(PartialProbe()(request), "ProtonVPN_v5.1.7_x64.exe"));

        var info = await new RemoteFileInfoProvider(stub.Client).GetFileInfoAsync(Url);

        Assert.Equal("ProtonVPN_v5.1.7_x64.exe", info.FileName);
    }

    // ------------------------------------------------------------- plumbing --

    private static Func<HttpRequestMessage, HttpResponseMessage> Head(
        long size, bool acceptRanges, string? fileName = null)
    {
        return _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Array.Empty<byte>()) };
            response.Content.Headers.ContentLength = size;
            if (acceptRanges) response.Headers.AcceptRanges.Add("bytes");
            return fileName is null ? response : Named(response, fileName);
        };
    }

    /// <summary>A server that honours the probe: one byte, and the total in Content-Range.</summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> PartialProbe() =>
        _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(new byte[] { 0x4D })
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, Size);
            response.Headers.AcceptRanges.Add("bytes");
            return response;
        };

    /// <summary>
    /// A cache answering a one-byte request with the entire file and a 200,
    /// while passing the origin's Accept-Ranges straight through. Nothing about
    /// this response is an error, which is the whole problem.
    /// </summary>
    private static Func<HttpRequestMessage, HttpResponseMessage> WholeFileProbe(bool acceptRanges = true) =>
        _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 0x4D, 0x5A }) };
            response.Content.Headers.ContentLength = Size;
            if (acceptRanges) response.Headers.AcceptRanges.Add("bytes");

            // An Age header is the tell that a cache, not the origin, answered.
            response.Headers.Age = TimeSpan.FromMinutes(30);
            return response;
        };

    private static HttpResponseMessage Named(HttpResponseMessage response, string fileName)
    {
        response.Content.Headers.ContentDisposition =
            new ContentDispositionHeaderValue("attachment") { FileName = fileName };
        return response;
    }

    /// <summary>
    /// Answers each request from a script, in order, and records what was asked.
    /// A script that runs out is a test asserting on the request count, so the
    /// last entry answers every request after it.
    /// </summary>
    private sealed class StubServer : IDisposable
    {
        private readonly Handler _handler;

        public StubServer(params Func<HttpRequestMessage, HttpResponseMessage>[] script)
        {
            _handler = new Handler(script);
            Client = new HttpClient(_handler);
        }

        public HttpClient Client { get; }

        public IReadOnlyList<Asked> Requests => _handler.Requests;

        public void Dispose() => Client.Dispose();

        /// <summary>What one request carried, captured before the message is disposed.</summary>
        public sealed record Asked(string Method, string? Range, string? CacheControl, string? Pragma);

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage>[] _script;

            public Handler(Func<HttpRequestMessage, HttpResponseMessage>[] script) => _script = script;

            public List<Asked> Requests { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(new Asked(
                    request.Method.Method,
                    request.Headers.Range?.ToString(),
                    request.Headers.CacheControl?.ToString(),
                    request.Headers.Pragma.Count > 0 ? request.Headers.Pragma.First().ToString() : null));

                var respond = _script[Math.Min(Requests.Count - 1, _script.Length - 1)];
                return Task.FromResult(respond(request));
            }
        }
    }
}
