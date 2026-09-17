using System.Text.Json.Serialization;
using QuickByte.Core.Helpers;

namespace QuickByte.Core.Models;

/// <summary>How QuickByte reaches the internet.</summary>
public enum ProxyMode
{
    /// <summary>Whatever Windows is set to (Settings &gt; Network &gt; Proxy), as the browsers do.</summary>
    System = 0,

    /// <summary>Straight to every server, whatever Windows says.</summary>
    None = 1,

    /// <summary>Through the proxy named in <see cref="ProxySettings"/>.</summary>
    Manual = 2
}

public enum ProxyKind
{
    Http = 0,
    Socks5 = 1,

    /// <summary>SOCKS4, sending host names for the proxy to resolve (4a) so lookups do not leak around it.</summary>
    Socks4 = 2
}

/// <summary>
/// The proxy downloads, probes and the update check go through. Persisted in
/// settings.json, with the password encrypted exactly as a download's is — see
/// <see cref="DownloadCredentials"/>.
/// </summary>
public sealed class ProxySettings
{
    public ProxyMode Mode { get; set; } = ProxyMode.System;

    public ProxyKind Kind { get; set; } = ProxyKind.Http;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 8080;

    public string UserName { get; set; } = string.Empty;

    /// <summary>The live password. Never serialized; <see cref="ProtectedPassword"/> is what reaches disk.</summary>
    [JsonIgnore]
    public string Password { get; set; } = string.Empty;

    public string? ProtectedPassword
    {
        get => SecretProtector.Protect(Password);
        set => Password = SecretProtector.Unprotect(value);
    }

    /// <summary>
    /// Addresses reached directly even through a manual proxy: host names,
    /// <c>*</c> wildcards, <c>.example.com</c> for a domain and all its subdomains,
    /// and <c>&lt;local&gt;</c> for names without a dot — the same forms Windows takes.
    /// See <see cref="ProxyBypass"/>. This machine is always reached directly.
    /// </summary>
    public string Bypass { get; set; } = string.Empty;

    /// <summary>A manual proxy with a host QuickByte could connect to, and a port that exists.</summary>
    [JsonIgnore]
    public bool IsComplete => IsValidHost(Host) && Port is > 0 and <= 65535;

    public ProxySettings Clone() => new()
    {
        Mode = Mode,
        Kind = Kind,
        Host = Host,
        Port = Port,
        UserName = UserName,
        Password = Password,
        Bypass = Bypass
    };

    /// <summary>
    /// A bare host name or IP address — no scheme, path, port or space. The value
    /// is built into a proxy address and, for FTP through an HTTP proxy, written
    /// into a request line, so anything else is refused rather than escaped.
    /// </summary>
    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253) return false;
        string trimmed = host.Trim('[', ']');
        if (System.Net.IPAddress.TryParse(trimmed, out _)) return true;
        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }
}
