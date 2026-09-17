using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;
using QuickByte.Core.Models;
using QuickByte.Core.Services.Ftp;
using QuickByte.Core.Services.Proxy;

namespace QuickByte.Core.Tests.Services.Proxy;

/// <summary>
/// Which requests go through the proxy, with what login, and whether a change in
/// Options reaches clients created long before it.
/// </summary>
public sealed class AppProxyTests
{
    private static AppProxy Manual(ProxyKind kind = ProxyKind.Socks5, string host = "proxy.example.com", int port = 1080,
        string user = "", string password = "", string bypass = "", IWebProxy? system = null)
    {
        var proxy = new AppProxy(system);
        proxy.Configure(new ProxySettings
        {
            Mode = ProxyMode.Manual, Kind = kind, Host = host, Port = port,
            UserName = user, Password = password, Bypass = bypass
        });
        return proxy;
    }

    [Theory]
    [InlineData(ProxyKind.Http, "http://proxy.example.com:1080/")]
    [InlineData(ProxyKind.Socks5, "socks5://proxy.example.com:1080/")]
    [InlineData(ProxyKind.Socks4, "socks4a://proxy.example.com:1080/")]
    public void A_manual_proxy_is_named_with_the_scheme_the_handler_speaks(ProxyKind kind, string expected)
    {
        Assert.Equal(expected, Manual(kind).GetProxy(new Uri("https://files.example.com/a.zip"))!.AbsoluteUri);
    }

    [Fact]
    public void An_ipv6_proxy_address_is_bracketed()
    {
        Assert.Equal("[2001:db8::7]", Manual(host: "2001:db8::7").GetProxy(new Uri("https://files.example.com/"))!.Host);
    }

    [Theory]
    [InlineData("http://localhost:9614/ping")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://app.localhost/")]
    public void This_machine_is_never_reached_through_a_proxy(string url)
    {
        var proxy = Manual();

        Assert.Null(proxy.GetProxy(new Uri(url)));
        Assert.True(proxy.IsBypassed(new Uri(url)));
    }

    [Fact]
    public void The_bypass_list_sends_its_hosts_directly()
    {
        var proxy = Manual(bypass: "*.lan; intranet.example.com");

        Assert.Null(proxy.GetProxy(new Uri("http://nas.lan/share.iso")));
        Assert.Null(proxy.GetProxy(new Uri("https://intranet.example.com/")));
        Assert.NotNull(proxy.GetProxy(new Uri("https://example.com/")));
    }

    [Fact]
    public void No_proxy_means_none_whatever_Windows_says()
    {
        var proxy = new AppProxy(new FixedProxy(new Uri("http://system-proxy:3128")));
        proxy.Configure(new ProxySettings { Mode = ProxyMode.None });

        Assert.Null(proxy.GetProxy(new Uri("https://example.com/")));
        Assert.Null(proxy.Credentials!.GetCredential(new Uri("http://system-proxy:3128"), "Basic"));
    }

    [Fact]
    public void Windows_settings_are_asked_and_their_answer_used()
    {
        var proxy = new AppProxy(new FixedProxy(new Uri("http://system-proxy:3128"), bypassed: "example.org"));

        Assert.Equal("http://system-proxy:3128/", proxy.GetProxy(new Uri("https://example.com/"))!.AbsoluteUri);
        Assert.Null(proxy.GetProxy(new Uri("https://example.org/")));
    }

    [Fact]
    public void Windows_settings_that_cannot_be_read_mean_no_proxy_rather_than_a_failed_download()
    {
        var proxy = new AppProxy(new ThrowingProxy());

        Assert.Null(proxy.GetProxy(new Uri("https://example.com/")));
    }

    [Fact]
    public void The_login_goes_only_to_a_proxy_the_user_named_and_follows_a_change_in_Options()
    {
        var proxy = Manual(user: "alice", password: "s3cret");
        var credentials = proxy.Credentials!;  // captured once, as the handler does

        var first = credentials.GetCredential(new Uri("socks5://proxy.example.com:1080"), "Basic")!;
        Assert.Equal(("alice", "s3cret"), (first.UserName, first.Password));

        proxy.Configure(new ProxySettings { Mode = ProxyMode.Manual, Host = "proxy.example.com", Port = 1080 });
        Assert.Null(credentials.GetCredential(new Uri("http://proxy.example.com:1080"), "Basic"));

        proxy.Configure(new ProxySettings { Mode = ProxyMode.System });
        Assert.Same(CredentialCache.DefaultNetworkCredentials, credentials.GetCredential(new Uri("http://system:3128"), "Negotiate"));
    }

    [Fact]
    public void A_manual_proxy_missing_its_host_connects_directly_rather_than_nowhere()
    {
        var proxy = new AppProxy(null);
        proxy.Configure(new ProxySettings { Mode = ProxyMode.Manual, Host = "", Port = 1080 });

        Assert.Null(proxy.GetProxy(new Uri("https://example.com/")));
        Assert.Null(proxy.TunnelFor(new Uri("ftp://example.com/")));
    }

    [Fact]
    public void Settings_are_copied_so_a_form_editing_them_later_changes_nothing()
    {
        var settings = new ProxySettings { Mode = ProxyMode.Manual, Host = "proxy.example.com", Port = 1080 };
        var proxy = new AppProxy(null);
        proxy.Configure(settings);

        settings.Host = "elsewhere.example.com";

        Assert.Equal("proxy.example.com", proxy.GetProxy(new Uri("https://example.com/"))!.Host);
    }

    [Fact]
    public void Ftp_tunnels_only_through_a_proxy_named_in_Options()
    {
        var ftp = new Uri("ftp://ftp.example.com/pub/file.iso");

        var endpoint = Manual(ProxyKind.Http, user: "bob", password: "pw").TunnelFor(ftp)!;
        Assert.Equal((ProxyKind.Http, "proxy.example.com", 1080, "bob"), (endpoint.Kind, endpoint.Host, endpoint.Port, endpoint.Credentials!.UserName));

        Assert.Null(new AppProxy(new FixedProxy(new Uri("http://system-proxy:3128"))).TunnelFor(ftp));
        Assert.Null(Manual(bypass: "ftp.example.com").TunnelFor(ftp));
    }

    [Fact]
    public async Task A_client_created_once_follows_the_proxy_from_one_request_to_the_next()
    {
        // The engine's clients are static; a proxy changed in Options must reach
        // them without a restart, credentials included.
        using var site = new TinyHttpServer("hello");
        using var first = new FakeSocks5Proxy(requiredLogin: ("alice", "one"));
        using var second = new FakeSocks5Proxy(requiredLogin: ("bob", "two"));

        var proxy = new AppProxy(null);
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = proxy, UseProxy = true, PooledConnectionLifetime = TimeSpan.Zero });
        string url = $"http://files.test:{site.Port}/a.txt";

        proxy.Configure(new ProxySettings { Mode = ProxyMode.Manual, Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = first.Port, UserName = "alice", Password = "one" });
        Assert.Equal("hello", await client.GetStringAsync(url));

        proxy.Configure(new ProxySettings { Mode = ProxyMode.Manual, Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = second.Port, UserName = "bob", Password = "two" });
        Assert.Equal("hello", await client.GetStringAsync(url));

        // The name went to the proxy to resolve; it resolves nowhere else.
        Assert.Equal(("files.test", site.Port), Assert.Single(first.Requests));
        Assert.Equal(("files.test", site.Port), Assert.Single(second.Requests));
        Assert.Equal(new[] { "bob:two" }, second.Logins);
    }

    private sealed class FixedProxy : IWebProxy
    {
        private readonly Uri _proxy;
        private readonly string? _bypassed;

        public FixedProxy(Uri proxy, string? bypassed = null)
        {
            _proxy = proxy;
            _bypassed = bypassed;
        }

        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => _proxy;
        public bool IsBypassed(Uri host) => host.Host == _bypassed;
    }

    private sealed class ThrowingProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => throw new InvalidOperationException("registry unreadable");
        public bool IsBypassed(Uri host) => throw new InvalidOperationException("registry unreadable");
    }
}

/// <summary>The three handshakes FTP tunnels through, checked byte for byte against scripted proxies.</summary>
public sealed class ProxyTunnelTests
{
    private static readonly byte[] Greeting = Encoding.ASCII.GetBytes("220 ready\r\n");

    private static ProxyEndpoint Endpoint(ProxyKind kind, int port, string? user = null, string? password = null) =>
        new(kind, "127.0.0.1", port, user is null ? null : new NetworkCredential(user, password));

    private static async Task<string> FirstLine(Stream stream)
    {
        var buffer = new byte[Greeting.Length];
        await stream.ReadExactlyAsync(buffer);
        return Encoding.ASCII.GetString(buffer);
    }

    [Fact]
    public async Task Socks5_sends_the_host_name_for_the_proxy_to_resolve_and_reads_no_further_than_its_reply()
    {
        byte[]? request = null;
        using var server = new ScriptedServer(async s =>
        {
            Assert.Equal(new byte[] { 5, 1, 0 }, await ScriptedServer.Read(s, 3));
            await s.WriteAsync(new byte[] { 5, 0 });
            request = await ScriptedServer.Read(s, 5 + "ftp.example.com".Length + 2);
            // The reply and the FTP greeting in one write, as a fast link delivers them.
            await s.WriteAsync(new byte[] { 5, 0, 0, 3, 4, (byte)'n', (byte)'a', (byte)'m', (byte)'e', 0, 21 }.Concat(Greeting).ToArray());
        });

        var (client, stream) = await ProxyTunnel.ConnectAsync("ftp.example.com", 21, Endpoint(ProxyKind.Socks5, server.Port), default);
        using (client)
        {
            Assert.Equal("220 ready\r\n", await FirstLine(stream));
        }

        await server.Completion;
        Assert.Equal(new byte[] { 5, 1, 0, 3, 15 }, request![..5]);
        Assert.Equal("ftp.example.com", Encoding.ASCII.GetString(request, 5, 15));
        Assert.Equal(new byte[] { 0, 21 }, request[^2..]);
    }

    [Fact]
    public async Task Socks5_signs_in_when_the_proxy_asks()
    {
        byte[]? login = null;
        using var server = new ScriptedServer(async s =>
        {
            Assert.Equal(new byte[] { 5, 2, 0, 2 }, await ScriptedServer.Read(s, 4));
            await s.WriteAsync(new byte[] { 5, 2 });
            login = await ScriptedServer.Read(s, 3 + 5 + 6);
            await s.WriteAsync(new byte[] { 1, 0 });
            await ScriptedServer.Read(s, 10); // CONNECT to an IPv4 address
            await s.WriteAsync(new byte[] { 5, 0, 0, 1, 1, 2, 3, 4, 0, 21 }.Concat(Greeting).ToArray());
        });

        var (client, stream) = await ProxyTunnel.ConnectAsync("192.0.2.10", 21, Endpoint(ProxyKind.Socks5, server.Port, "alice", "s3cret"), default);
        using (client) Assert.Equal("220 ready\r\n", await FirstLine(stream));

        await server.Completion;
        Assert.Equal(new byte[] { 1, 5 }, login![..2]);
        Assert.Equal("alice", Encoding.UTF8.GetString(login, 2, 5));
        Assert.Equal(6, login[7]);
        Assert.Equal("s3cret", Encoding.UTF8.GetString(login, 8, 6));
    }

    [Fact]
    public async Task A_rejected_socks5_login_is_a_proxy_login_failure_not_a_network_one()
    {
        using var server = new ScriptedServer(async s =>
        {
            await ScriptedServer.Read(s, 4);
            await s.WriteAsync(new byte[] { 5, 2 });
            await ScriptedServer.Read(s, 3 + 5 + 5);
            await s.WriteAsync(new byte[] { 1, 1 });
        });

        var ex = await Assert.ThrowsAsync<ProxyAuthenticationException>(
            () => ProxyTunnel.ConnectAsync("ftp.example.com", 21, Endpoint(ProxyKind.Socks5, server.Port, "alice", "wrong"), default));
        Assert.Equal(ProxyAuthenticationException.RejectedMessage, ex.Message);
    }

    [Fact]
    public async Task A_socks5_proxy_that_wants_a_login_nobody_gave_says_so()
    {
        using var server = new ScriptedServer(async s =>
        {
            await ScriptedServer.Read(s, 3);
            await s.WriteAsync(new byte[] { 5, 0xFF });
        });

        var ex = await Assert.ThrowsAsync<ProxyAuthenticationException>(
            () => ProxyTunnel.ConnectAsync("ftp.example.com", 21, Endpoint(ProxyKind.Socks5, server.Port), default));
        Assert.Equal(ProxyAuthenticationException.NeededMessage, ex.Message);
    }

    [Fact]
    public async Task A_socks5_refusal_names_the_reason()
    {
        using var server = new ScriptedServer(async s =>
        {
            await ScriptedServer.Read(s, 3);
            await s.WriteAsync(new byte[] { 5, 0 });
            await ScriptedServer.Read(s, 5 + 15 + 2);
            await s.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 });
        });

        var ex = await Assert.ThrowsAsync<IOException>(
            () => ProxyTunnel.ConnectAsync("ftp.example.com", 21, Endpoint(ProxyKind.Socks5, server.Port), default));
        Assert.Contains("refused", ex.Message);
    }

    [Fact]
    public async Task Socks4a_sends_the_host_name_after_the_user_id()
    {
        byte[]? request = null;
        using var server = new ScriptedServer(async s =>
        {
            request = await ScriptedServer.Read(s, 8 + "bob".Length + 1 + "ftp.example.com".Length + 1);
            await s.WriteAsync(new byte[] { 0, 0x5A, 0, 0, 0, 0, 0, 0 }.Concat(Greeting).ToArray());
        });

        var (client, stream) = await ProxyTunnel.ConnectAsync("ftp.example.com", 2121, Endpoint(ProxyKind.Socks4, server.Port, "bob", ""), default);
        using (client) Assert.Equal("220 ready\r\n", await FirstLine(stream));

        await server.Completion;
        Assert.Equal(new byte[] { 4, 1, 2121 >> 8, 2121 & 0xFF, 0, 0, 0, 1 }, request![..8]);
        Assert.Equal("bob\0ftp.example.com\0", Encoding.ASCII.GetString(request, 8, request.Length - 8));
    }

    [Fact]
    public async Task Http_connect_asks_for_the_host_with_the_proxy_login_and_leaves_the_greeting_unread()
    {
        string? head = null;
        using var server = new ScriptedServer(async s =>
        {
            head = await ScriptedServer.ReadHead(s);
            await s.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\nVia: test\r\n\r\n").Concat(Greeting).ToArray());
        });

        var (client, stream) = await ProxyTunnel.ConnectAsync("ftp.example.com", 21, Endpoint(ProxyKind.Http, server.Port, "alice", "pw"), default);
        using (client) Assert.Equal("220 ready\r\n", await FirstLine(stream));

        await server.Completion;
        Assert.StartsWith("CONNECT ftp.example.com:21 HTTP/1.1\r\nHost: ftp.example.com:21\r\n", head);
        Assert.Contains("Proxy-Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:pw")), head);
    }

    [Theory]
    [InlineData("HTTP/1.1 407 Proxy Authentication Required", typeof(ProxyAuthenticationException))]
    [InlineData("HTTP/1.1 403 Forbidden", typeof(IOException))]
    public async Task An_http_proxy_that_will_not_connect_says_why(string statusLine, Type expected)
    {
        using var server = new ScriptedServer(async s =>
        {
            await ScriptedServer.ReadHead(s);
            await s.WriteAsync(Encoding.ASCII.GetBytes(statusLine + "\r\nContent-Length: 0\r\n\r\n"));
        });

        var ex = await Assert.ThrowsAnyAsync<IOException>(
            () => ProxyTunnel.ConnectAsync("ftp.example.com", 21, Endpoint(ProxyKind.Http, server.Port), default));
        Assert.IsType(expected, ex);
        if (expected == typeof(IOException)) Assert.Contains("403", ex.Message);
    }

    [Fact]
    public async Task A_proxy_that_is_not_there_is_reported_as_the_proxy()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var ex = await Assert.ThrowsAsync<IOException>(
            () => ProxyTunnel.ConnectAsync("ftp.example.com", 21, Endpoint(ProxyKind.Socks5, port), default));
        Assert.Contains("proxy", ex.Message);
    }

    [Fact]
    public async Task An_ftp_download_runs_through_a_socks5_proxy_control_and_data_alike()
    {
        byte[] file = Enumerable.Range(0, 70_000).Select(i => (byte)(i * 7)).ToArray();
        using var ftp = new TinyFtpServer(file);
        using var socks = new FakeSocks5Proxy(requiredLogin: ("carol", "pw"));
        var proxy = new AppProxy(null);
        proxy.Configure(new ProxySettings { Mode = ProxyMode.Manual, Kind = ProxyKind.Socks5, Host = "127.0.0.1", Port = socks.Port, UserName = "carol", Password = "pw" });

        await using var channel = await FtpControlChannel.ConnectAsync(new Uri($"ftp://ftp.test:{ftp.Port}/pub/file.bin"), null, default, proxy);
        await using var data = await channel.OpenReadAsync("/pub/file.bin", 0, default);
        using var received = new MemoryStream();
        await data.CopyToAsync(received);

        Assert.Equal(file, received.ToArray());

        // Both connections were the proxy's to make, to the name in the address —
        // never the private address the server announced for passive mode.
        var requests = socks.Requests.ToList();
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.Equal("ftp.test", r.Host));
        Assert.Equal(ftp.Port, requests[0].Port);
    }
}

public sealed class ProxyBypassTests
{
    [Fact]
    public void A_list_is_read_in_any_of_the_separators_Windows_accepts()
    {
        Assert.Equal(
            new[] { "example.com", "*.lan", "<local>", "10.*" },
            ProxyBypass.Parse(" https://Example.com/;*.LAN, <local>\n10.* ;example.com"));
    }

    [Theory]
    [InlineData("example.com", "https://example.com/a", true)]
    [InlineData("example.com", "https://www.example.com/a", false)]
    [InlineData("*.example.com", "https://www.example.com/a", true)]
    [InlineData("*.example.com", "https://notexample.com/a", false)]
    [InlineData(".example.com", "https://example.com/", true)]
    [InlineData(".example.com", "https://deep.cdn.example.com/", true)]
    [InlineData(".example.com", "https://badexample.com/", false)]
    [InlineData("10.*", "http://10.1.2.3/x", true)]
    [InlineData("10.*", "http://110.1.2.3/x", false)]
    [InlineData("<local>", "http://nas/share", true)]
    [InlineData("<local>", "http://nas.lan/share", false)]
    [InlineData("<local>", "http://10.0.0.1/", false)]
    [InlineData("files.example.com:8080", "http://files.example.com:8080/", true)]
    [InlineData("files.example.com:8080", "http://files.example.com/", false)]
    [InlineData("[2001:db8::1]", "http://[2001:db8::1]/", true)]
    public void Entries_match_as_Windows_reads_them(string entry, string url, bool expected)
    {
        Assert.Equal(expected, ProxyBypass.Matches(ProxyBypass.Parse(entry), new Uri(url)));
    }

    [Fact]
    public void A_list_longer_than_anyone_types_is_cut_short()
    {
        string list = string.Join(";", Enumerable.Range(0, 1000).Select(i => $"host{i}.example.com"));

        Assert.Equal(ProxyBypass.MaxEntries, ProxyBypass.Parse(list).Count);
    }
}

public sealed class ProxySettingsTests
{
    [Fact]
    public void The_proxy_password_never_reaches_settings_json_in_the_clear()
    {
        var settings = new DownloadSettings
        {
            Proxy = new ProxySettings { Mode = ProxyMode.Manual, Host = "proxy.example.com", Port = 3128, UserName = "alice", Password = "correct horse" }
        };

        string json = JsonSerializer.Serialize(settings);
        var back = JsonSerializer.Deserialize<DownloadSettings>(json)!;

        Assert.DoesNotContain("correct horse", json);
        Assert.Equal("correct horse", back.Proxy.Password);
        Assert.Equal((ProxyMode.Manual, "proxy.example.com", 3128, "alice"), (back.Proxy.Mode, back.Proxy.Host, back.Proxy.Port, back.Proxy.UserName));
    }

    [Fact]
    public void A_settings_file_with_no_proxy_or_a_null_one_uses_Windows_settings()
    {
        Assert.Equal(ProxyMode.System, JsonSerializer.Deserialize<DownloadSettings>("{}")!.Proxy.Mode);
        Assert.Equal(ProxyMode.System, JsonSerializer.Deserialize<DownloadSettings>("{\"Proxy\":null}")!.Proxy.Mode);
    }

    [Theory]
    [InlineData("proxy.example.com", true)]
    [InlineData("10.0.0.8", true)]
    [InlineData("2001:db8::7", true)]
    [InlineData("[2001:db8::7]", true)]
    [InlineData("", false)]
    [InlineData("http://proxy.example.com", false)]
    [InlineData("proxy.example.com:8080", false)]
    [InlineData("proxy example.com", false)]
    [InlineData("proxy.example.com\r\nX-Injected: 1", false)]
    public void A_proxy_host_is_a_bare_name_or_address(string host, bool valid)
    {
        Assert.Equal(valid, ProxySettings.IsValidHost(host));
    }
}
