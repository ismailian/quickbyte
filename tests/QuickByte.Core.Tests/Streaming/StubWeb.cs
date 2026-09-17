using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace QuickByte.Core.Tests.Streaming;

/// <summary>
/// A handful of web addresses served from memory: playlists, pieces and keys. It
/// honours <c>Range</c> unless told not to, can fail an address a set number of
/// times, and can end a body early while still promising all of it — the three
/// server behaviours a stream download has to survive and no real server offers
/// on demand.
/// </summary>
internal sealed class StubWeb : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, Resource> _resources = new();

    public ConcurrentQueue<(string Url, string? Range, string? Cookie)> Requests { get; } = new();

    public HttpClient Client => new(this, disposeHandler: false);

    public void Add(string url, string text, string contentType = "application/vnd.apple.mpegurl") =>
        Add(url, Encoding.UTF8.GetBytes(text), contentType);

    public void Add(string url, byte[] body, string contentType = "application/octet-stream", bool honoursRanges = true) =>
        _resources[url] = new Resource(body, contentType, honoursRanges);

    public void Remove(string url) => _resources.TryRemove(url, out _);

    /// <summary>The next <paramref name="times"/> requests for <paramref name="url"/> answer 500.</summary>
    public void Fail(string url, int times) => _resources[url].FailuresLeft = times;

    /// <summary>The next request for <paramref name="url"/> sends only <paramref name="bytes"/> of its body.</summary>
    public void CutShort(string url, int bytes) => _resources[url].CutAt = bytes;

    public int CountRequests(string url) => Requests.Count(r => r.Url == url);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string url = request.RequestUri!.AbsoluteUri;
        request.Headers.TryGetValues("Cookie", out var cookies);
        Requests.Enqueue((url, request.Headers.Range?.ToString(), cookies?.FirstOrDefault()));

        if (!_resources.TryGetValue(url, out var resource))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });

        if (resource.FailuresLeft > 0)
        {
            resource.FailuresLeft--;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request });
        }

        long from = 0, to = resource.Body.Length - 1;
        var status = HttpStatusCode.OK;
        if (request.Headers.Range?.Ranges.SingleOrDefault() is { } range && resource.HonoursRanges)
        {
            from = range.From ?? 0;
            to = Math.Min(range.To ?? to, to);
            status = HttpStatusCode.PartialContent;
        }

        byte[] slice = resource.Body.AsSpan((int)from, (int)(to - from + 1)).ToArray();
        byte[] sent = slice;
        if (resource.CutAt is int cut)
        {
            resource.CutAt = null;
            sent = slice[..Math.Min(cut, slice.Length)];
        }

        var content = new ByteArrayContent(sent);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(resource.ContentType);
        content.Headers.ContentLength = slice.Length; // promised in full, even when cut short
        if (status == HttpStatusCode.PartialContent)
            content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, resource.Body.Length);

        return Task.FromResult(new HttpResponseMessage(status) { Content = content, RequestMessage = request });
    }

    private sealed class Resource
    {
        public Resource(byte[] body, string contentType, bool honoursRanges)
        {
            Body = body;
            ContentType = contentType;
            HonoursRanges = honoursRanges;
        }

        public byte[] Body { get; }
        public string ContentType { get; }
        public bool HonoursRanges { get; }
        public volatile int FailuresLeft;
        public int? CutAt;
    }
}
