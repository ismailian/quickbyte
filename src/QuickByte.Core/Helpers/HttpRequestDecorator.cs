using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using QuickByte.Core.Models;

namespace QuickByte.Core.Helpers;

/// <summary>
/// Stamps a <see cref="RequestOptions"/> onto an outgoing
/// <see cref="HttpRequestMessage"/>. One place for it because the metadata
/// probe and every download connection have to present *identical* credentials
/// and headers — a probe that authenticates and connections that don't would
/// resolve a real file size and then fetch eight copies of a login page.
/// </summary>
internal static class HttpRequestDecorator
{
    public static void Apply(HttpRequestMessage request, RequestOptions? options)
        => Apply(request, options, AppUserAgent.Current);

    /// <param name="userAgent">
    /// The User-Agent to present. A seam, and the only reason this overload
    /// exists: <see cref="AppUserAgent.Current"/> is process-wide live state, and
    /// the Core suite runs its tests in parallel, so a test that configured it
    /// would be read by whatever else was mid-assertion.
    /// </param>
    internal static void Apply(HttpRequestMessage request, RequestOptions? options, string userAgent)
    {
        // Before the early return below, and deliberately: the User-Agent is an
        // app-wide setting rather than part of a request's options, and a request
        // made with no options at all still has to carry one. The metadata
        // probe's client sets no default of its own, so for that path this is the
        // only thing that identifies QuickByte at all.
        ApplyUserAgent(request, options, userAgent);

        if (options is null) return;

        if (options.Credentials is { IsEmpty: false } credentials)
        {
            // Sent up front rather than after a 401. HttpClient's own
            // challenge-response handling needs a per-request handler to carry
            // the credentials, and every connection here shares one static
            // client on purpose; presenting Basic pre-emptively also saves a
            // round trip on every one of up to 32 connections.
            string pair = $"{credentials.UserName}:{credentials.Password}";
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
        }

        if (options.BypassCache)
        {
            // Both forms, because the intermediary that has to hear it is not
            // necessarily a modern one: Pragma is the HTTP/1.0 spelling and is
            // still the only thing some proxies act on. See
            // RequestOptions.BypassCache for what this is worked around.
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            request.Headers.Pragma.Add(new NameValueHeaderValue("no-cache"));
        }

        if (options.Headers is null) return;

        foreach (var (name, value) in options.Headers)
        {
            // Without validation: these come from the browser extension, and a
            // Cookie or Referer the browser itself was happy to send must not be
            // rejected here over HttpClient's stricter parsing. Range and
            // Authorization are dropped because this request owns them — a
            // stale captured Range would silently truncate the segment.
            if (name.Equals("Range", StringComparison.OrdinalIgnoreCase)) continue;

            // Conditionals likewise belong to the request that makes them. A
            // captured If-None-Match turns the probe into a 304 with no size, and
            // a stale If-Range would decide for the server which version counts.
            if (IsConditional(name)) continue;
            if (options.HasCredentials && name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;

            // Same rule for the pair just written above: a captured
            // "Cache-Control: max-age=0" appended beside our own no-cache is a
            // header with two directives, and which one an intermediary honours
            // is its business, not ours.
            if (options.BypassCache
                && (name.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Pragma", StringComparison.OrdinalIgnoreCase))) continue;

            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    /// <summary>
    /// Writes the User-Agent, unless something more specific owns it.
    ///
    /// A captured one wins over the user's setting, and the setting over
    /// QuickByte's own — most specific first. The browser extension's is the most
    /// specific there is: it is the string the browser paired with the
    /// <c>Cookie</c> captured alongside it, and a server that pins a session to
    /// the agent it was issued to answers a mismatched pair with the sign-in page
    /// the capture existed to avoid. It is written by the header loop above, so
    /// all this does is stay out of its way — appending ours too would put two
    /// agents on one request.
    /// </summary>
    private static void ApplyUserAgent(HttpRequestMessage request, RequestOptions? options, string userAgent)
    {
        if (string.IsNullOrEmpty(userAgent)) return;

        // Already set by the caller, or by an earlier Apply on the same request.
        if (request.Headers.Contains("User-Agent")) return;

        if (options?.Headers is { } headers)
        {
            foreach (var (name, _) in headers)
            {
                if (name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) return;
            }
        }

        // Without validation, for the reason the captured headers above are: the
        // value's job is to look like whatever a server will accept, and
        // HttpClient parses a User-Agent more strictly than servers write them.
        // AppUserAgent.Normalize has already refused the only thing that is
        // actually unsafe here, a control character.
        request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
    }

    /// <summary>
    /// The <c>If-Range</c> condition for a download's version: its strong ETag,
    /// else its Last-Modified date, else nothing.
    ///
    /// A weak ETag yields nothing rather than falling back to the date. RFC 9110
    /// forbids a weak tag in <c>If-Range</c> — a server must treat it as a
    /// mismatch and send the whole file every time — and it forbids the date when
    /// the client holds an entity tag for the representation at all.
    /// </summary>
    public static RangeConditionHeaderValue? IfRangeFor(RemoteFileVersion? version)
    {
        if (version is null) return null;

        if (version.ETag is not null)
        {
            return version.HasStrongETag && EntityTagHeaderValue.TryParse(version.ETag, out var tag)
                ? new RangeConditionHeaderValue(tag)
                : null;
        }

        return version.LastModified is { } date ? new RangeConditionHeaderValue(date) : null;
    }

    private static bool IsConditional(string name) =>
        name.Equals("If-Range", StringComparison.OrdinalIgnoreCase)
        || name.Equals("If-Match", StringComparison.OrdinalIgnoreCase)
        || name.Equals("If-None-Match", StringComparison.OrdinalIgnoreCase)
        || name.Equals("If-Modified-Since", StringComparison.OrdinalIgnoreCase)
        || name.Equals("If-Unmodified-Since", StringComparison.OrdinalIgnoreCase);
}
