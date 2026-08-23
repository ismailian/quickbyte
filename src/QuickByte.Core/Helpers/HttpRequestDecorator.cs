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
    {
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

        if (options.Headers is null) return;

        foreach (var (name, value) in options.Headers)
        {
            // Without validation: these come from the browser extension, and a
            // Cookie or Referer the browser itself was happy to send must not be
            // rejected here over HttpClient's stricter parsing. Range and
            // Authorization are dropped because this request owns them — a
            // stale captured Range would silently truncate the segment.
            if (name.Equals("Range", StringComparison.OrdinalIgnoreCase)) continue;
            if (options.HasCredentials && name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) continue;

            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
