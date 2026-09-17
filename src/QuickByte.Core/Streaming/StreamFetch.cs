using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;
using QuickByte.Core.Models;

namespace QuickByte.Core.Streaming;

/// <summary>
/// Reads a playlist or manifest off the web, the way the pieces it lists will be
/// read: with the download's cookies, referrer and login, since a player on a
/// signed-in page is typically the only thing its playlist would answer.
/// </summary>
internal static class StreamFetch
{
    /// <summary>A playlist for a ten-hour video is a few megabytes. Past this it is not a playlist.</summary>
    public const int MaxDocumentBytes = 16 * 1024 * 1024;

    /// <returns>The text, and the address it finally came from — relative piece addresses resolve against that, not the one asked for.</returns>
    public static async Task<(string Text, Uri FinalUri)> GetTextAsync(
        HttpClient client, Uri uri, RequestOptions? options, CancellationToken cancellationToken)
    {
        if (!StreamPlan.IsWeb(uri)) throw new FormatException("A stream's playlist has to be on the web (http or https).");

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        HttpRequestDecorator.Apply(request, options);

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(response, options);

        if (response.Content.Headers.ContentLength > MaxDocumentBytes)
            throw new FormatException("This is too large to be a playlist.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = await ReadAtMostAsync(stream, MaxDocumentBytes, cancellationToken).ConfigureAwait(false)
            ?? throw new FormatException("This is too large to be a playlist.");

        return (Encoding.UTF8.GetString(bytes), response.RequestMessage?.RequestUri ?? uri);
    }

    /// <summary>
    /// The one place a stream's HTTP answer is judged, so a playlist, a piece and a
    /// key report a refused login identically — as the exception the Add window
    /// turns into a sign-in prompt, and which is never retried.
    /// </summary>
    public static void EnsureSuccess(HttpResponseMessage response, RequestOptions? options)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.ProxyAuthenticationRequired)
        {
            throw new AuthenticationRequiredException(
                options?.HasCredentials == true
                    ? "The server rejected the supplied user name or password."
                    : "The server requires a user name and password.")
            { CredentialsWereSupplied = options?.HasCredentials == true };
        }

        response.EnsureSuccessStatusCode();
    }

    /// <summary>The whole stream, or null if it holds more than <paramref name="limit"/> bytes.</summary>
    public static async Task<byte[]?> ReadAtMostAsync(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
