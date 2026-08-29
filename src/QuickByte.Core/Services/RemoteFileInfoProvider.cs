using System.Threading;
using System.Net;
using System.Net.Http;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// Resolves remote file metadata over HTTP(S). A HEAD request first (cheap),
/// then a ranged GET probe (<c>bytes=0-0</c>) which is the only thing that can
/// answer the question the pool actually needs answered: not whether the server
/// says it supports ranges, but whether asking for one gets a range back.
///
/// A 401 is reported as <see cref="AuthenticationRequiredException"/> rather
/// than a generic failure: it is the one error the user can actually do
/// something about, and the Add Download dialog turns it into a credentials
/// prompt instead of a dead end.
/// </summary>
/// <remarks>
/// <para><b>Why the probe is not optional.</b> HEAD used to be allowed to
/// settle range support on its own when it also supplied a size and a name, on
/// the strength of <c>Accept-Ranges: bytes</c>. That header is a claim, and the
/// thing making it is frequently a CDN edge rather than the origin — one which,
/// holding the whole file in cache, answers a <c>Range</c> request with the
/// whole body and a <c>200</c> while passing the origin's <c>Accept-Ranges</c>
/// through unchanged. Nothing about that response is an error in HTTP. It just
/// means the file gets split eight ways and seven of those connections receive
/// bytes belonging at offset zero — a download that fails outright, and does so
/// only on the servers that look the most cooperative.</para>
///
/// <para><b>Why a 200 to the probe is not the end of it.</b> The same
/// contradiction — claims ranges, ignores one — is the signature of a cache in
/// the middle rather than of a server that cannot seek. So the probe is asked a
/// second time with <c>Cache-Control: no-cache</c>, which is what makes a shared
/// cache go and ask the origin. When that comes back <c>206</c>, ranges work,
/// and <see cref="RemoteFileInfo.BypassCache"/> records that they only work when
/// asked for that way, so every connection asks the same way.
/// (protonvpn.com's download endpoint behaves exactly like this; it is the
/// reason any of this is here.)</para>
/// </remarks>
public sealed class RemoteFileInfoProvider : IRemoteFileInfoProvider
{
    private static readonly HttpClient SharedClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),

        // Same reason as HttpConnectionFactory: captured browser cookies arrive
        // as an explicit header and must be the only ones on the request.
        UseCookies = false
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    private readonly HttpClient _client;

    public RemoteFileInfoProvider() : this(SharedClient) { }

    /// <summary>
    /// The seam the tests need, and the only one. Everything this class exists
    /// to get right is about servers that answer *incorrectly* — a range request
    /// met with the whole file, an Accept-Ranges header the response contradicts
    /// — and there is no way to ask a real server to behave like that on demand.
    /// Internal rather than public: production has exactly one client, and it is
    /// shared on purpose.
    /// </summary>
    internal RemoteFileInfoProvider(HttpClient client) => _client = client;

    public async Task<RemoteFileInfo> GetFileInfoAsync(string url, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var info = new RemoteFileInfo
        {
            FinalUrl = url,

            // Carried in, not reset: a download that already learned it has to
            // ask past a cache probes that way from the start, and the probe
            // below must not then read its own success as "no bypass needed"
            // and strip the headers off every connection.
            BypassCache = options?.BypassCache == true
        };

        string? serverFileName = null;
        bool headAnsweredEverything = false;

        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            HttpRequestDecorator.Apply(headRequest, options);
            using var headResponse = await _client.SendAsync(headRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            ThrowIfChallenged(headResponse, info, options);

            if (headResponse.IsSuccessStatusCode)
            {
                serverFileName = Absorb(info, headResponse);

                // Enough to name and size the file, but never enough to decide
                // how many connections fetch it. That is what the probe is for.
                headAnsweredEverything = info.HasKnownSize && serverFileName is not null;
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
            // Some servers reject HEAD entirely — fall through to the GET probe.
        }

        try
        {
            // Not "serverFileName ??= await ..." — that short-circuits, and the
            // one case it would skip the probe in is a HEAD that answered
            // everything, which is exactly the case this exists to stop
            // trusting.
            string? probedName = await ProbeRangeSupportAsync(url, info, options, cancellationToken).ConfigureAwait(false);
            serverFileName ??= probedName;
        }
        catch (Exception ex) when (headAnsweredEverything
                                   && ex is not OperationCanceledException
                                   && ex is not AuthenticationRequiredException)
        {
            // HEAD already answered the questions the dialog asks. A probe that
            // could not run leaves range support unproven — one connection, and
            // a download that works — rather than throwing away a good answer.
            info.SupportsRangeRequests = false;
        }

        return Finish(info, serverFileName);
    }

    /// <summary>
    /// Asks for one byte and reports what came back. Sets
    /// <see cref="RemoteFileInfo.SupportsRangeRequests"/> only on a genuine
    /// <c>206</c>, and escalates past an intermediary cache once when the
    /// response contradicts the server's own <c>Accept-Ranges</c> claim.
    /// </summary>
    /// <returns>The file name the server named on the probe, or null.</returns>
    private async Task<string?> ProbeRangeSupportAsync(
        string url, RemoteFileInfo info, RequestOptions? options, CancellationToken cancellationToken)
    {
        using var response = await SendRangedProbeAsync(url, options, cancellationToken).ConfigureAwait(false);

        ThrowIfChallenged(response, info, options);
        response.EnsureSuccessStatusCode();

        string? fileName = Absorb(info, response, fromRangedProbe: true);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            info.SupportsRangeRequests = true;
            return fileName;
        }

        // A whole-file answer to a one-byte request. If the server never claimed
        // to do ranges, that is simply the truth and costs one connection. If it
        // did claim it, something between here and the origin is answering from
        // a copy it cannot slice — and that is worth one more request to check.
        // Not re-checked when the caller already asked past the cache: the same
        // request cannot produce a different answer, and the probe would loop.
        if (!info.ServerClaimsRangeSupport || info.BypassCache) return fileName;

        using var revalidated = await SendRangedProbeAsync(
            url, (options ?? RequestOptions.None).WithCacheBypass(), cancellationToken).ConfigureAwait(false);

        if (revalidated.StatusCode != HttpStatusCode.PartialContent) return fileName;

        info.SupportsRangeRequests = true;
        info.BypassCache = true;
        return Absorb(info, revalidated, fromRangedProbe: true) ?? fileName;
    }

    private async Task<HttpResponseMessage> SendRangedProbeAsync(
        string url, RequestOptions? options, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        HttpRequestDecorator.Apply(request, options);

        // ResponseHeadersRead: a server that ignores the range is about to start
        // sending the entire file, and the status line is all this needs.
        // Disposing the response is what stops it.
        return await _client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
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

        // Recorded as the claim it is. Only a 206 sets SupportsRangeRequests —
        // see the remarks on this class for what this header is worth on its own.
        if (response.Headers.AcceptRanges.Contains("bytes"))
            info.ServerClaimsRangeSupport = true;

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
