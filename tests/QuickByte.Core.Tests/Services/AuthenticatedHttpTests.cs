using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Models;
using QuickByte.Core.Services;

namespace QuickByte.Core.Tests.Services;

/// <summary>
/// Servers that want more than Basic. Digest is checked end to end against a server
/// that really verifies the response hash; NTLM, which needs a Windows account to
/// finish, is checked as far as the client opening the handshake with it.
/// </summary>
public sealed class AuthenticatedHttpTests
{
    private static readonly byte[] File = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();

    private static HttpClient RealClient() => new(HttpConnectionFactory.CreateHandler());

    private static RequestOptions Login(string user, string password) =>
        new() { Credentials = new DownloadCredentials { UserName = user, Password = password } };

    [Fact]
    public async Task A_digest_server_is_answered_and_the_segment_downloads_intact()
    {
        using var server = new AuthServer(AuthServer.Mode.Digest, "alice", "s3cret", File);
        using var folder = new TempFolder();
        using var client = RealClient();

        var connection = new DownloadConnection(client, 0, server.Url, 1000, 2999, 0, folder.File("part0.tmp"),
            new DownloadSettings { MaxRetries = 0 }, options: Login("alice", "s3cret"));
        await connection.RunAsync(CancellationToken.None);

        Assert.Equal(File[1000..3000], System.IO.File.ReadAllBytes(folder.File("part0.tmp")));

        // Basic first, as always; refused with a Digest challenge; then Digest.
        var schemes = server.Authorizations.Select(a => a?.Split(' ')[0]).ToList();
        Assert.Equal("Basic", schemes[0]);
        Assert.Equal("Digest", schemes[^1]);
    }

    [Fact]
    public async Task Once_a_server_has_asked_for_digest_later_requests_go_straight_to_it()
    {
        using var server = new AuthServer(AuthServer.Mode.Digest, "bob", "pw", File);
        using var folder = new TempFolder();
        using var client = RealClient();
        var options = Login("bob", "pw");

        await new DownloadConnection(client, 0, server.Url, 0, 99, 0, folder.File("a.tmp"), new DownloadSettings { MaxRetries = 0 }, options: options)
            .RunAsync(CancellationToken.None);
        int before = server.Authorizations.Count(a => a?.StartsWith("Basic", StringComparison.Ordinal) == true);

        await new DownloadConnection(client, 1, server.Url, 100, 199, 0, folder.File("b.tmp"), new DownloadSettings { MaxRetries = 0 }, options: options)
            .RunAsync(CancellationToken.None);

        Assert.Equal(before, server.Authorizations.Count(a => a?.StartsWith("Basic", StringComparison.Ordinal) == true));
        Assert.Equal(File[100..200], System.IO.File.ReadAllBytes(folder.File("b.tmp")));
    }

    [Fact]
    public async Task A_wrong_password_for_digest_is_still_a_rejected_password()
    {
        using var server = new AuthServer(AuthServer.Mode.Digest, "carol", "right", File);
        using var folder = new TempFolder();
        using var client = RealClient();

        var connection = new DownloadConnection(client, 0, server.Url, 0, 99, 0, folder.File("c.tmp"),
            new DownloadSettings { MaxRetries = 3 }, options: Login("carol", "wrong"));

        var thrown = await Assert.ThrowsAsync<AuthenticationRequiredException>(() => connection.RunAsync(CancellationToken.None));
        Assert.True(thrown.CredentialsWereSupplied);
    }

    [Fact]
    public async Task The_probe_signs_in_with_digest_too()
    {
        using var server = new AuthServer(AuthServer.Mode.Digest, "dave", "pw", File);
        using var client = RealClient();

        var info = await new RemoteFileInfoProvider(client).GetFileInfoAsync(server.Url, Login("dave", "pw"));

        Assert.Equal(File.Length, info.ContentLength);
        Assert.True(info.SupportsRangeRequests);
    }

    [Fact]
    public async Task An_ntlm_server_is_offered_the_ntlm_handshake_and_never_basic_again()
    {
        using var server = new AuthServer(AuthServer.Mode.Ntlm, "CORP\\erin", "pw", File);
        using var folder = new TempFolder();
        using var client = RealClient();

        var connection = new DownloadConnection(client, 0, server.Url, 0, 99, 0, folder.File("d.tmp"),
            new DownloadSettings { MaxRetries = 0 }, options: Login("CORP\\erin", "pw"));

        // The scripted server cannot complete NTLM — that needs a Windows account —
        // so the request ends refused. What matters is what the client sent.
        await Assert.ThrowsAsync<AuthenticationRequiredException>(() => connection.RunAsync(CancellationToken.None));

        var sent = server.Authorizations.Where(a => a is not null).ToList();
        Assert.StartsWith("Basic", sent[0]);
        // "TlRMTVNTUAAB" is base64 for NTLMSSP followed by message type 1, the negotiate message.
        Assert.Contains(sent.Skip(1), a => a!.StartsWith("NTLM TlRMTVNTUAAB", StringComparison.Ordinal));
        Assert.DoesNotContain(sent.Skip(1), a => a!.StartsWith("Basic", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_a_login_a_challenge_is_simply_the_answer()
    {
        using var server = new AuthServer(AuthServer.Mode.Ntlm, "x", "y", File);
        using var client = RealClient();

        using var response = await AuthenticatedHttp.SendAsync(client, () => new HttpRequestMessage(HttpMethod.Get, server.Url), null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Single(server.Authorizations);
    }

    [Fact]
    public void A_challenge_that_came_from_another_host_is_not_answered_with_the_login()
    {
        // A redirect to a third party that asks for NTLM must not be handed the
        // user's credentials for the server they were typed for.
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://elsewhere.example.net/file")
        };
        response.Headers.WwwAuthenticate.ParseAdd("NTLM");

        Assert.False(AuthenticatedHttp.WantsChallengeResponse(response, new Uri("https://files.example.com/file")));
        Assert.True(AuthenticatedHttp.WantsChallengeResponse(response, new Uri("https://elsewhere.example.net/other")));
    }

    [Fact]
    public void A_401_offering_only_basic_is_a_rejected_password_not_a_handshake()
    {
        var response = new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://files.example.com/") };
        response.Headers.WwwAuthenticate.ParseAdd("Basic realm=\"files\"");

        Assert.False(AuthenticatedHttp.WantsChallengeResponse(response, new Uri("https://files.example.com/")));
    }

    [Theory]
    [InlineData("CORP\\erin", "erin", "CORP")]
    [InlineData("erin@corp.example.com", "erin@corp.example.com", "")]
    [InlineData("erin", "erin", "")]
    [InlineData("\\erin", "\\erin", "")]
    public void A_windows_login_is_split_into_domain_and_user(string typed, string user, string domain)
    {
        var credential = AuthenticatedHttp.ToNetworkCredential(new DownloadCredentials { UserName = typed, Password = "pw" });

        Assert.Equal((user, domain, "pw"), (credential.UserName, credential.Domain, credential.Password));
    }

    /// <summary>
    /// An HTTP/1.1 server with keep-alive that serves one file by ranges, behind Digest
    /// (verified: RFC 7616, MD5, qop=auth) or behind an NTLM challenge it cannot finish.
    /// </summary>
    private sealed class AuthServer : IDisposable
    {
        public enum Mode { Digest, Ntlm }

        private const string Realm = "files";
        private const string Nonce = "dcd98b7102dd2f0e8b11d0f600bfb0c093";

        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Mode _mode;
        private readonly string _user;
        private readonly string _password;
        private readonly byte[] _file;

        public AuthServer(Mode mode, string user, string password, byte[] file)
        {
            _mode = mode;
            _user = user;
            _password = password;
            _file = file;
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/files/data.bin";

        public ConcurrentQueue<string?> Authorizations { get; } = new();

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    while (true)
                    {
                        string head = await ReadHeadAsync(stream);
                        if (head.Length == 0) return;

                        string[] lines = head.Split("\r\n");
                        string[] requestLine = lines[0].Split(' ');
                        string? authorization = Header(lines, "Authorization");
                        Authorizations.Enqueue(authorization);

                        if (!Authorized(requestLine[0], authorization))
                        {
                            string challenge = _mode == Mode.Digest
                                ? $"WWW-Authenticate: Digest realm=\"{Realm}\", qop=\"auth\", nonce=\"{Nonce}\", algorithm=MD5\r\nWWW-Authenticate: Basic realm=\"{Realm}\"\r\n"
                                : "WWW-Authenticate: NTLM\r\n";
                            await Write(stream, $"HTTP/1.1 401 Unauthorized\r\n{challenge}Content-Length: 0\r\n\r\n", Array.Empty<byte>());
                            continue;
                        }

                        long from = 0, to = _file.Length - 1;
                        var range = Header(lines, "Range") is { } r ? Regex.Match(r, @"bytes=(\d+)-(\d*)") : Match.Empty;
                        string status = "200 OK", contentRange = string.Empty;
                        if (range.Success)
                        {
                            from = long.Parse(range.Groups[1].Value);
                            if (range.Groups[2].Value.Length > 0) to = Math.Min(to, long.Parse(range.Groups[2].Value));
                            status = "206 Partial Content";
                            contentRange = $"Content-Range: bytes {from}-{to}/{_file.Length}\r\n";
                        }

                        byte[] body = requestLine[0] == "HEAD" ? Array.Empty<byte>() : _file[(int)from..((int)to + 1)];
                        await Write(stream, $"HTTP/1.1 {status}\r\n{contentRange}Accept-Ranges: bytes\r\nContent-Length: {(requestLine[0] == "HEAD" ? _file.Length : body.Length)}\r\n\r\n", body);
                    }
                }
                catch
                {
                    // The client closed the connection.
                }
            }
        }

        private bool Authorized(string method, string? authorization)
        {
            if (_mode == Mode.Ntlm || authorization is null || !authorization.StartsWith("Digest ", StringComparison.Ordinal)) return false;

            var fields = Regex.Matches(authorization[7..], @"(\w+)=(?:""([^""]*)""|([^,\s]*))")
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value, StringComparer.OrdinalIgnoreCase);

            string Md5(string text) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

            if (fields.GetValueOrDefault("username") != _user || fields.GetValueOrDefault("nonce") != Nonce) return false;
            string ha1 = Md5($"{_user}:{Realm}:{_password}");
            string ha2 = Md5($"{method}:{fields.GetValueOrDefault("uri")}");
            string expected = Md5($"{ha1}:{Nonce}:{fields.GetValueOrDefault("nc")}:{fields.GetValueOrDefault("cnonce")}:{fields.GetValueOrDefault("qop")}:{ha2}");
            return string.Equals(expected, fields.GetValueOrDefault("response"), StringComparison.OrdinalIgnoreCase);
        }

        private static string? Header(string[] lines, string name) =>
            lines.Skip(1).Where(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
                .Select(l => l[(name.Length + 1)..].Trim()).FirstOrDefault();

        private static async Task<string> ReadHeadAsync(NetworkStream stream)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (!(bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n'))
            {
                if (await stream.ReadAsync(one) == 0) return string.Empty;
                bytes.Add(one[0]);
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        private static async Task Write(NetworkStream stream, string head, byte[] body)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
            if (body.Length > 0) await stream.WriteAsync(body);
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
