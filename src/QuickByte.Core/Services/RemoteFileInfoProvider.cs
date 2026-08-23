using System.Threading;
using System.Net;
using System.Net.Http;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// Resolves remote file metadata over HTTP(S). Tries a HEAD request first
/// (cheap); if the server doesn't answer HEAD usefully, falls back to a ranged
/// GET probe (bytes=0-0) which also lets us positively confirm Range support
/// via a 206 Partial Content response.
///
/// A 401 is reported as <see cref="AuthenticationRequiredException"/> rather
/// than a generic failure: it is the one error the user can actually do
/// something about, and the Add Download dialog turns it into a credentials
/// prompt instead of a dead end.
/// </summary>
public sealed class RemoteFileInfoProvider : IRemoteFileInfoProvider
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),

        // Same reason as HttpConnectionFactory: captured browser cookies arrive
        // as an explicit header and must be the only ones on the request.
        UseCookies = false
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<RemoteFileInfo> GetFileInfoAsync(string url, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var info = new RemoteFileInfo { FinalUrl = url };
        string? serverFileName = null;

        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            HttpRequestDecorator.Apply(headRequest, options);
            using var headResponse = await Client.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            ThrowIfChallenged(headResponse, info, options);

            if (headResponse.IsSuccessStatusCode)
            {
                serverFileName = Absorb(info, headResponse);

                // The probe is skipped only when HEAD answered *both* questions.
                // Plenty of servers set Content-Disposition in their GET handler
                // and not on HEAD, and stopping here on the strength of a
                // Content-Length alone is what leaves those files named after
                // the last segment of their URL.
                if (info.HasKnownSize && serverFileName is not null) return Finish(info, serverFileName);
            }
        }
        catch (AuthenticationRequiredException)
        {
            // The one HEAD failure worth surfacing: a ranged GET would only be
            // refused the same way, and re-probing costs the user a second wait
            // before the same prompt.
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Some servers reject HEAD entirely — fall through to a ranged GET probe.
        }

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
        getRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        HttpRequestDecorator.Apply(getRequest, options);
        using var getResponse = await Client.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        ThrowIfChallenged(getResponse, info, options);
        getResponse.EnsureSuccessStatusCode();

        if (getResponse.StatusCode == HttpStatusCode.PartialContent)
            info.SupportsRangeRequests = true;

        serverFileName ??= Absorb(info, getResponse, fromRangedProbe: true);

        return Finish(info, serverFileName);
    }

    private static void ThrowIfChallenged(HttpResponseMessage response, RemoteFileInfo info, RequestOptions? options)
    {
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.ProxyAuthenticationRequired))
            return;

        bool supplied = options?.HasCredentials == true;
        info.RequiresAuthentication = true;
        throw new AuthenticationRequiredException(
            supplied
                ? "The server rejected that user name or password."
                : "This file needs a user name and password.")
        { CredentialsWereSupplied = supplied };
    }

    /// <summary>
    /// Copies what a response knows into <paramref name="info"/> and returns the
    /// file name the server named explicitly, or null if it named none.
    /// </summary>
    private static string? Absorb(RemoteFileInfo info, HttpResponseMessage response, bool fromRangedProbe = false)
    {
        // First, before anything derives a name from it: HttpClient follows
        // redirects itself, so RequestUri is the address the bytes will really
        // come from. A link like ".../7z2301-x64.exe/download" only reveals its
        // file name here.
        if (response.RequestMessage?.RequestUri is not null)
            info.FinalUrl = response.RequestMessage.RequestUri.ToString();

        if (response.Headers.AcceptRanges.Contains("bytes"))
            info.SupportsRangeRequests = true;

        // A ranged probe's own Content-Length is 1 byte, which must never become
        // the file's size. Only Content-Range carries the total, and a probe the
        // server answered with a full 200 carries it in Content-Length as usual.
        if (fromRangedProbe)
        {
            if (response.Content.Headers.ContentRange?.Length is long total && total > 0)
                info.ContentLength = total;
            else if (response.StatusCode != HttpStatusCode.PartialContent
                     && response.Content.Headers.ContentLength is long full && full > 0)
                info.ContentLength = full;
        }
        else if (response.Content.Headers.ContentLength is long length && length > 0)
        {
            info.ContentLength = length;
        }

        if (response.Content.Headers.ContentType?.MediaType is { Length: > 0 } mediaType)
            info.ContentType = mediaType;

        info.ETag ??= response.Headers.ETag?.Tag;
        info.LastModified ??= response.Content.Headers.LastModified;

        // FileNameStar is the RFC 5987 form and already decoded by the BCL; it
        // wins because it is the one that can carry non-ASCII correctly.
        string? fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                           ?? response.Content.Headers.ContentDisposition?.FileName;

        fileName = fileName?.Trim().Trim('"');
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    /// <summary>
    /// Settles the file name: what the server said, else what the *final* URL
    /// says, and in either case with an extension inferred from the content type
    /// if it still has none.
    /// </summary>
    private static RemoteFileInfo Finish(RemoteFileInfo info, string? serverFileName)
    {
        string name = serverFileName is not null
            ? FileNameHelper.SanitizeFileName(serverFileName)
            : FileNameHelper.FileNameFromUrl(info.FinalUrl);

        info.FileName = FileNameHelper.EnsureExtension(name, info.ContentType);
        return info;
    }
}
