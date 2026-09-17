using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// Signs a request in when the server wants more than HTTP Basic — Digest, NTLM or
/// Negotiate, the last two being what a Windows server or an intranet file share
/// usually asks for.
///
/// Basic is still sent up front by <see cref="Helpers.HttpRequestDecorator"/>: it
/// costs nothing, and every connection sharing one client could not do the
/// challenge-response on its own. The other schemes cannot be sent up front at all —
/// Digest answers a nonce the server issues, and NTLM and Negotiate are a handshake
/// bound to one TCP connection — and <see cref="SocketsHttpHandler"/> does all three
/// only when its <c>Credentials</c> hold the login, which a client shared by every
/// download cannot. So a request whose 401 offers one of them is sent again through
/// a client of its own for that login and that server, which does the handshake,
/// and the server is remembered so later requests go straight there.
/// </summary>
/// <remarks>
/// The login is registered for the server the request was made to and nowhere
/// else — a <see cref="CredentialCache"/> matches by origin — so a redirect to
/// another host is never answered with it, and a challenge that came from a
/// redirect target is not retried. Clients are kept per origin, login and timeout,
/// never disposed while the app runs, as the shared clients are.
/// </remarks>
internal static class AuthenticatedHttp
{
    private static readonly string[] ChallengeSchemes = { "Negotiate", "NTLM", "Digest" };

    /// <summary>Origin and login pairs whose server has asked for a challenge-response scheme.</summary>
    private static readonly ConcurrentDictionary<string, byte> NeedsChallenge = new();

    private static readonly ConcurrentDictionary<string, Lazy<HttpClient>> Clients = new();

    /// <summary>
    /// Sends the request <paramref name="build"/> makes through <paramref name="client"/>,
    /// or through a challenge-response client when the server has asked for one.
    /// <paramref name="build"/> may be called twice: a request cannot be sent again.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> build,
        RequestOptions? options,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var credentials = options?.Credentials;
        if (credentials is not { IsEmpty: false })
        {
            using var anonymous = build();
            return await client.SendAsync(anonymous, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }

        using (var first = build())
        {
            var origin = first.RequestUri!;
            string key = Key(origin, credentials, timeout);

            if (!NeedsChallenge.ContainsKey(key))
            {
                var response = await client.SendAsync(first, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!WantsChallengeResponse(response, origin)) return response;

                NeedsChallenge.TryAdd(key, 0);
                response.Dispose();
            }
        }

        using var request = build();

        // The handler only does its handshake for a request that carries no
        // Authorization of its own — and the pre-emptive Basic one was just refused.
        request.Headers.Authorization = null;
        return await ClientFor(request.RequestUri!, credentials, timeout)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// A 401, from the server the request was made to, offering a scheme the handler can
    /// answer and Basic cannot. A 401 offering only Basic is a rejected password, and
    /// asking again with the same one would not change that.
    /// </summary>
    internal static bool WantsChallengeResponse(HttpResponseMessage response, Uri requested)
    {
        if (response.StatusCode != HttpStatusCode.Unauthorized) return false;

        var answeredBy = response.RequestMessage?.RequestUri;
        if (answeredBy is not null && !SameOrigin(answeredBy, requested)) return false;

        return response.Headers.WwwAuthenticate.Any(challenge =>
            ChallengeSchemes.Contains(challenge.Scheme, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Forgets which servers asked for a handshake. For the tests.</summary>
    internal static void Forget() => NeedsChallenge.Clear();

    private static HttpClient ClientFor(Uri uri, DownloadCredentials credentials, TimeSpan? timeout) =>
        Clients.GetOrAdd(Key(uri, credentials, timeout), _ => new Lazy<HttpClient>(() => Create(uri, credentials, timeout))).Value;

    private static HttpClient Create(Uri uri, DownloadCredentials credentials, TimeSpan? timeout)
    {
        var origin = new Uri(uri.GetLeftPart(UriPartial.Authority));
        var login = ToNetworkCredential(credentials);

        // Every scheme the handler knows, for this origin only.
        var cache = new CredentialCache();
        foreach (string scheme in new[] { "Negotiate", "NTLM", "Digest", "Basic" })
            cache.Add(origin, scheme, login);

        var handler = HttpConnectionFactory.CreateHandler();
        handler.Credentials = cache;
        handler.PreAuthenticate = true;

        var client = new HttpClient(handler) { Timeout = timeout ?? Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(HttpConnectionFactory.UserAgent);
        return client;
    }

    /// <summary>
    /// <c>DOMAIN\user</c> is split into its domain and user for NTLM and Negotiate;
    /// <c>user@domain</c> is passed whole, which is how Windows reads it.
    /// </summary>
    internal static NetworkCredential ToNetworkCredential(DownloadCredentials credentials)
    {
        string user = credentials.UserName;
        int slash = user.IndexOf('\\');
        return slash > 0 && slash < user.Length - 1
            ? new NetworkCredential(user[(slash + 1)..], credentials.Password, user[..slash])
            : new NetworkCredential(user, credentials.Password);
    }

    /// <summary>The origin, the user, and a hash of the password — the key never holds the password itself.</summary>
    private static string Key(Uri uri, DownloadCredentials credentials, TimeSpan? timeout)
    {
        string secret = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credentials.UserName + "\0" + credentials.Password)));
        return $"{uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant()}|{secret}|{timeout?.Ticks ?? -1}";
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.GetLeftPart(UriPartial.Authority), b.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
}
