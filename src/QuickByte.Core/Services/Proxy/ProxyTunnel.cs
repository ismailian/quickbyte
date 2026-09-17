using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services.Proxy;

/// <summary>
/// A TCP connection to a host, opened through a proxy when one is configured —
/// for FTP, which <see cref="System.Net.Http.HttpClient"/>'s own proxy support
/// never sees: its control channel and every passive data connection are raw
/// sockets.
///
/// Three handshakes: SOCKS5 (RFC 1928, with RFC 1929 user name and password),
/// SOCKS4a, and HTTP <c>CONNECT</c>. Host names are handed to the proxy rather
/// than resolved here, so the lookup happens where the connection does and does
/// not leak around the proxy. Every reply is read to exactly its own length: the
/// FTP server's greeting can arrive in the same packet as the proxy's answer, and
/// a byte read too many is a greeting lost.
/// </summary>
internal static class ProxyTunnel
{
    /// <summary>An HTTP proxy's answer to CONNECT is a status line and a few headers; past this it is not one.</summary>
    private const int MaxConnectResponseBytes = 16 * 1024;

    public static async Task<(TcpClient Client, Stream Stream)> ConnectAsync(
        string host, int port, ProxyEndpoint? proxy, CancellationToken cancellationToken)
    {
        var client = new TcpClient();
        try
        {
            if (proxy is null)
            {
                await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
                return (client, client.GetStream());
            }

            try
            {
                await client.ConnectAsync(proxy.Host, proxy.Port, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                throw new IOException($"Could not reach the proxy {proxy.Host}:{proxy.Port}: {ex.Message}", ex);
            }

            var stream = client.GetStream();
            switch (proxy.Kind)
            {
                case ProxyKind.Socks5:
                    await Socks5Async(stream, host, port, proxy.Credentials, cancellationToken).ConfigureAwait(false);
                    break;
                case ProxyKind.Socks4:
                    await Socks4aAsync(stream, host, port, proxy.Credentials, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await HttpConnectAsync(stream, host, port, proxy.Credentials, cancellationToken).ConfigureAwait(false);
                    break;
            }
            return (client, stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------- SOCKS5 --

    private static async Task Socks5Async(Stream stream, string host, int port, NetworkCredential? credentials, CancellationToken token)
    {
        bool offerPassword = credentials is { UserName.Length: > 0 };
        byte[] greeting = offerPassword ? new byte[] { 5, 2, 0x00, 0x02 } : new byte[] { 5, 1, 0x00 };
        await stream.WriteAsync(greeting, token).ConfigureAwait(false);

        var choice = await ReadAsync(stream, 2, token).ConfigureAwait(false);
        if (choice[0] != 5) throw new IOException("The proxy did not answer as a SOCKS5 proxy.");

        switch (choice[1])
        {
            case 0x00:
                break;
            case 0x02 when offerPassword:
                await Socks5LoginAsync(stream, credentials!, token).ConfigureAwait(false);
                break;
            case 0x02:
                throw new ProxyAuthenticationException(ProxyAuthenticationException.NeededMessage);
            default:
                throw new ProxyAuthenticationException(offerPassword
                    ? "The SOCKS5 proxy accepts none of the ways QuickByte can sign in."
                    : ProxyAuthenticationException.NeededMessage);
        }

        using var request = new MemoryStream();
        request.Write(new byte[] { 5, 1, 0 }); // version, CONNECT, reserved
        WriteSocks5Address(request, host);
        request.WriteByte((byte)(port >> 8));
        request.WriteByte((byte)port);
        await stream.WriteAsync(request.ToArray(), token).ConfigureAwait(false);

        var head = await ReadAsync(stream, 4, token).ConfigureAwait(false);
        if (head[0] != 5) throw new IOException("The proxy did not answer as a SOCKS5 proxy.");
        if (head[1] != 0) throw new IOException($"The SOCKS5 proxy could not connect to {host}:{port}: {Socks5Reason(head[1])}.");

        // The address the proxy bound: read past, to exactly its length.
        int addressLength = head[3] switch
        {
            1 => 4,
            4 => 16,
            3 => (await ReadAsync(stream, 1, token).ConfigureAwait(false))[0],
            _ => throw new IOException("The SOCKS5 proxy answered with an address type that does not exist.")
        };
        await ReadAsync(stream, addressLength + 2, token).ConfigureAwait(false);
    }

    private static async Task Socks5LoginAsync(Stream stream, NetworkCredential credentials, CancellationToken token)
    {
        byte[] user = Encoding.UTF8.GetBytes(credentials.UserName);
        byte[] password = Encoding.UTF8.GetBytes(credentials.Password ?? string.Empty);
        if (user.Length > 255 || password.Length > 255)
            throw new ProxyAuthenticationException("The proxy's user name or password is too long for SOCKS5 (255 bytes each).");

        var login = new byte[3 + user.Length + password.Length];
        login[0] = 1;
        login[1] = (byte)user.Length;
        user.CopyTo(login, 2);
        login[2 + user.Length] = (byte)password.Length;
        password.CopyTo(login, 3 + user.Length);
        await stream.WriteAsync(login, token).ConfigureAwait(false);

        var status = await ReadAsync(stream, 2, token).ConfigureAwait(false);
        if (status[1] != 0) throw new ProxyAuthenticationException(ProxyAuthenticationException.RejectedMessage);
    }

    private static void WriteSocks5Address(Stream request, string host)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out var address))
        {
            request.WriteByte(address.AddressFamily == AddressFamily.InterNetworkV6 ? (byte)4 : (byte)1);
            request.Write(address.GetAddressBytes());
            return;
        }

        byte[] name = Encoding.ASCII.GetBytes(new IdnMapping().GetAscii(host));
        if (name.Length is 0 or > 255) throw new IOException($"'{host}' is not a host name a SOCKS5 proxy can be asked for.");
        request.WriteByte(3);
        request.WriteByte((byte)name.Length);
        request.Write(name);
    }

    private static string Socks5Reason(byte code) => code switch
    {
        1 => "the proxy failed",
        2 => "its rules do not allow it",
        3 => "the network is unreachable",
        4 => "the host is unreachable",
        5 => "the connection was refused",
        6 => "it timed out",
        7 => "the proxy does not support connecting",
        8 => "the proxy does not support that kind of address",
        _ => $"error {code}"
    };

    // ------------------------------------------------------------ SOCKS4a --

    private static async Task Socks4aAsync(Stream stream, string host, int port, NetworkCredential? credentials, CancellationToken token)
    {
        using var request = new MemoryStream();
        request.Write(new byte[] { 4, 1, (byte)(port >> 8), (byte)port });

        bool isAddress = IPAddress.TryParse(host.Trim('[', ']'), out var address);
        if (isAddress && address!.AddressFamily != AddressFamily.InterNetwork)
            throw new IOException("A SOCKS4 proxy cannot connect to an IPv6 address.");

        // 0.0.0.x (x ≠ 0) is 4a's signal that a host name follows the user id.
        request.Write(isAddress ? address!.GetAddressBytes() : new byte[] { 0, 0, 0, 1 });
        request.Write(Encoding.UTF8.GetBytes(credentials?.UserName ?? string.Empty));
        request.WriteByte(0);
        if (!isAddress)
        {
            request.Write(Encoding.ASCII.GetBytes(new IdnMapping().GetAscii(host)));
            request.WriteByte(0);
        }
        await stream.WriteAsync(request.ToArray(), token).ConfigureAwait(false);

        var reply = await ReadAsync(stream, 8, token).ConfigureAwait(false);
        switch (reply[1])
        {
            case 0x5A:
                return;
            case 0x5C or 0x5D:
                throw new ProxyAuthenticationException(ProxyAuthenticationException.RejectedMessage);
            default:
                throw new IOException($"The SOCKS4 proxy could not connect to {host}:{port}.");
        }
    }

    // --------------------------------------------------------- HTTP CONNECT --

    private static async Task HttpConnectAsync(Stream stream, string host, int port, NetworkCredential? credentials, CancellationToken token)
    {
        string bare = host.Trim('[', ']');
        string authority = IPAddress.TryParse(bare, out var address) && address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"[{bare}]:{port}"
            : $"{host}:{port}";

        var request = new StringBuilder()
            .Append("CONNECT ").Append(authority).Append(" HTTP/1.1\r\n")
            .Append("Host: ").Append(authority).Append("\r\n");
        if (credentials is { UserName.Length: > 0 })
        {
            string pair = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{credentials.UserName}:{credentials.Password}"));
            request.Append("Proxy-Authorization: Basic ").Append(pair).Append("\r\n");
        }
        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), token).ConfigureAwait(false);

        string statusLine = await ReadConnectResponseAsync(stream, token).ConfigureAwait(false);
        string[] parts = statusLine.Split(' ', 3);
        if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int status))
            throw new IOException("The proxy did not answer as an HTTP proxy.");

        if (status is >= 200 and < 300) return;
        if (status == 407)
        {
            throw new ProxyAuthenticationException(credentials is { UserName.Length: > 0 }
                ? ProxyAuthenticationException.RejectedMessage
                : ProxyAuthenticationException.NeededMessage);
        }
        throw new IOException($"The proxy would not connect to {authority}: {statusLine}");
    }

    /// <summary>The response head, a byte at a time so nothing after its blank line is consumed. Returns the status line.</summary>
    private static async Task<string> ReadConnectResponseAsync(Stream stream, CancellationToken token)
    {
        var head = new List<byte>(256);
        var one = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(one, token).ConfigureAwait(false) == 0)
                throw new IOException("The proxy closed the connection.");
            head.Add(one[0]);

            int n = head.Count;
            if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n') break;
            if (n >= MaxConnectResponseBytes) throw new IOException("The proxy's answer never ended.");
        }

        string text = Encoding.ASCII.GetString(head.ToArray());
        return text[..text.IndexOf('\r')];
    }

    private static async Task<byte[]> ReadAsync(Stream stream, int count, CancellationToken token)
    {
        var buffer = new byte[count];
        try
        {
            await stream.ReadExactlyAsync(buffer, token).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new IOException("The proxy closed the connection.", ex);
        }
        return buffer;
    }
}
