using System.Net;
using System.Net.Http;
using QuickByte.Core.Helpers;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services.Proxy;

/// <summary>A proxy an FTP connection tunnels through; see <see cref="ProxyTunnel"/>.</summary>
public sealed record ProxyEndpoint(ProxyKind Kind, string Host, int Port, NetworkCredential? Credentials);

/// <summary>
/// The one proxy every HTTP client in the engine is handed — the downloads, the
/// probe, the playlist reader and the update check — answering from whatever
/// Options last said.
///
/// A live object rather than a setting read at start-up, for the reason the speed
/// limit is one: the clients are static and created once, and
/// <see cref="SocketsHttpHandler"/> cannot be given a different proxy after its
/// first request. It does ask its proxy for every request, though, and pools
/// connections by the proxy it was told, so answering from the current settings
/// is all it takes for a change in Options to apply to the next request.
///
/// <see cref="ProxyMode.System"/> hands the question to Windows' own settings —
/// what every client did before this existed — with the signed-in user's
/// credentials for a proxy that challenges, as a browser offers them.
/// </summary>
public sealed class AppProxy : IWebProxy
{
    /// <summary>The instance the engine's shared clients use. Configured by the composition root.</summary>
    public static AppProxy Shared { get; } = new();

    private readonly IWebProxy? _system;
    private volatile Snapshot _current;

    public AppProxy() : this(SystemProxyOrNull()) { }

    /// <summary>For the tests: Windows' proxy settings are the machine's, not a test's.</summary>
    internal AppProxy(IWebProxy? system)
    {
        _system = system;
        _current = Build(new ProxySettings());
        Credentials = new LiveCredentials(this);
    }

    public void Configure(ProxySettings? settings) => _current = Build((settings ?? new ProxySettings()).Clone());

    public ProxyMode Mode => _current.Settings.Mode;

    public Uri? GetProxy(Uri destination)
    {
        var current = _current;
        if (ProxyBypass.IsLocal(destination)) return null;

        switch (current.Settings.Mode)
        {
            case ProxyMode.None:
                return null;

            case ProxyMode.Manual:
                return current.ProxyUri is null || current.Bypassed(destination) ? null : current.ProxyUri;

            default:
                try
                {
                    if (_system is null || _system.IsBypassed(destination)) return null;
                    var proxy = _system.GetProxy(destination);
                    return proxy is null || proxy == destination ? null : proxy;
                }
                catch
                {
                    return null; // an unreadable system setting is no proxy, as it was before
                }
        }
    }

    public bool IsBypassed(Uri host) => GetProxy(host) is null;

    /// <summary>
    /// Read by the handler when a proxy challenges, and captured by it once — so
    /// this is a fixed object that looks the current settings up each time.
    /// </summary>
    public ICredentials? Credentials { get; set; }

    /// <summary>
    /// Where an FTP connection to <paramref name="destination"/> should tunnel, or
    /// null to connect directly. Only a manual proxy is tunnelled through: Windows'
    /// settings name a proxy for the web, and an HTTP proxy asked to CONNECT to
    /// port 21 is one that almost always refuses.
    /// </summary>
    public ProxyEndpoint? TunnelFor(Uri destination)
    {
        var current = _current;
        if (current.Settings.Mode != ProxyMode.Manual || current.ProxyUri is null) return null;
        if (ProxyBypass.IsLocal(destination) || current.Bypassed(destination)) return null;

        return new ProxyEndpoint(current.Settings.Kind, current.Settings.Host.Trim('[', ']'), current.Settings.Port, current.NetworkCredential);
    }

    private static Snapshot Build(ProxySettings settings)
    {
        Uri? proxyUri = null;
        NetworkCredential? credential = null;

        if (settings.Mode == ProxyMode.Manual && settings.IsComplete)
        {
            string scheme = settings.Kind switch
            {
                ProxyKind.Socks5 => "socks5",
                ProxyKind.Socks4 => "socks4a",
                _ => "http"
            };
            string host = settings.Host.Trim();
            if (IPAddress.TryParse(host.Trim('[', ']'), out var address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                host = $"[{address}]";

            proxyUri = new Uri($"{scheme}://{host}:{settings.Port}");
            if (settings.UserName.Length > 0) credential = new NetworkCredential(settings.UserName, settings.Password);
        }

        return new Snapshot(settings, proxyUri, credential, ProxyBypass.Parse(settings.Bypass));
    }

    private static IWebProxy? SystemProxyOrNull()
    {
        try { return HttpClient.DefaultProxy; }
        catch { return null; }
    }

    private sealed record Snapshot(ProxySettings Settings, Uri? ProxyUri, NetworkCredential? NetworkCredential, IReadOnlyList<string> Bypass)
    {
        public bool Bypassed(Uri destination)
        {
            try { return ProxyBypass.Matches(Bypass, destination); }
            catch { return false; } // a pathological pattern goes through the proxy, as it would without the list
        }
    }

    private sealed class LiveCredentials : ICredentials
    {
        private readonly AppProxy _owner;

        public LiveCredentials(AppProxy owner) => _owner = owner;

        public NetworkCredential? GetCredential(Uri uri, string authType)
        {
            var current = _owner._current;
            return current.Settings.Mode switch
            {
                // Only ever to the proxy the user named, and only what they typed.
                ProxyMode.Manual => current.NetworkCredential,

                // A proxy Windows is set to use, challenging with NTLM or Negotiate:
                // the signed-in user's own credentials, which is what a browser sends.
                ProxyMode.System => CredentialCache.DefaultNetworkCredentials,

                _ => null
            };
        }
    }
}
