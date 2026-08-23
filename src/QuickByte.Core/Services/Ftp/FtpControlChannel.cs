using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using QuickByte.Core.Exceptions;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services.Ftp;

/// <summary>
/// A single FTP control connection plus the one data connection it is currently
/// running: connect, log in, ask for metadata, and open a binary read at a byte
/// offset.
///
/// Written directly on <see cref="TcpClient"/> rather than through
/// <c>FtpWebRequest</c>. The BCL's FTP client has been obsolete since .NET 6
/// (SYSLIB0014, and there is no non-obsolete way to construct one), and it hides
/// exactly the two things a download manager needs most: which passive port a
/// segment is actually using, and whether the server honoured <c>REST</c> —
/// without which resume silently writes the wrong bytes into a chunk file.
///
/// One channel serves one transfer. Segmented downloads open several, each with
/// its own control connection and its own <c>REST</c> offset, because FTP has no
/// equivalent of an HTTP Range header: the only way to bound a segment is to
/// start it at an offset and stop reading at the end of it.
/// </summary>
internal sealed class FtpControlChannel : IAsyncDisposable
{
    private const int DefaultPort = 21;
    private const int ConnectTimeoutMilliseconds = 30_000;

    /// <summary>What anonymous FTP wants in the password field: any e-mail-shaped string.</summary>
    private const string AnonymousUser = "anonymous";
    private const string AnonymousPassword = "quickbyte@example.com";

    private static readonly Regex PassivePattern = new(@"\((\d+),(\d+),(\d+),(\d+),(\d+),(\d+)\)", RegexOptions.Compiled);
    private static readonly Regex ExtendedPassivePattern = new(@"\(\|\|\|(\d+)\|\)", RegexOptions.Compiled);

    private readonly TcpClient _control;
    private readonly string _host;
    private readonly bool _secure;
    private readonly Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private Stream _stream;
    private StreamReader _reader;
    private TcpClient? _data;
    private Stream? _dataStream;
    private bool _protectData;

    private FtpControlChannel(TcpClient control, Stream stream, string host, bool secure)
    {
        _control = control;
        _stream = stream;
        _host = host;
        _secure = secure;
        _reader = new StreamReader(_stream, _encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
    }

    /// <summary>
    /// Opens the control connection, negotiates TLS for an <c>ftps://</c> URL,
    /// logs in (anonymously when <paramref name="credentials"/> is null or
    /// empty) and switches to binary mode.
    /// </summary>
    public static async Task<FtpControlChannel> ConnectAsync(Uri uri, DownloadCredentials? credentials, CancellationToken cancellationToken)
    {
        bool secure = uri.Scheme.Equals("ftps", StringComparison.OrdinalIgnoreCase);
        int port = uri.IsDefaultPort || uri.Port <= 0 ? DefaultPort : uri.Port;

        var client = new TcpClient();
        FtpControlChannel? channel = null;
        try
        {
            // TcpClient has no connect timeout of its own; a linked token gives
            // one without leaving a half-open socket behind on an unreachable
            // host, which is the usual way an FTP URL fails.
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connectTimeout.CancelAfter(ConnectTimeoutMilliseconds);
            await client.ConnectAsync(uri.Host, port, connectTimeout.Token).ConfigureAwait(false);

            channel = new FtpControlChannel(client, client.GetStream(), uri.Host, secure);
            await channel.HandshakeAsync(credentials, cancellationToken).ConfigureAwait(false);
            return channel;
        }
        catch
        {
            if (channel is not null) await channel.DisposeAsync().ConfigureAwait(false);
            else client.Dispose();
            throw;
        }
    }

    private async Task HandshakeAsync(DownloadCredentials? credentials, CancellationToken cancellationToken)
    {
        var greeting = await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
        if (greeting.Code != 220)
            throw new IOException($"The FTP server refused the connection: {greeting.Text}");

        if (_secure) await NegotiateTlsAsync(cancellationToken).ConfigureAwait(false);

        // Best-effort, before any path is sent: without it a server defaulting to
        // Latin-1 mis-reads a non-ASCII file name and answers 550 for a file that
        // is plainly there. A server that doesn't know OPTS simply says no.
        await SendAsync("OPTS UTF8 ON", cancellationToken).ConfigureAwait(false);

        bool named = credentials is { UserName.Length: > 0 };
        string user = named ? credentials!.UserName : AnonymousUser;
        string password = named ? credentials!.Password : AnonymousPassword;

        var reply = await SendAsync($"USER {user}", cancellationToken).ConfigureAwait(false);

        // 331 is the common path (password wanted); 230 means the server logged
        // us in on the user name alone and there is nothing left to send.
        if (reply.Code == 331)
            reply = await SendAsync($"PASS {password}", cancellationToken).ConfigureAwait(false);

        if (reply.Code != 230)
        {
            if (reply.IsAuthenticationFailure || reply.Code == 331 || reply.Code == 332)
            {
                bool supplied = credentials is { IsEmpty: false };
                throw new AuthenticationRequiredException(
                    supplied
                        ? $"The FTP server rejected that user name or password ({Summarize(reply)})."
                        : "This FTP server does not allow anonymous access — a user name and password are needed.")
                { CredentialsWereSupplied = supplied };
            }

            throw new IOException($"FTP login failed: {Summarize(reply)}");
        }

        var type = await SendAsync("TYPE I", cancellationToken).ConfigureAwait(false);
        if (!type.IsPositive)
            throw new IOException($"The FTP server refused binary mode: {Summarize(type)}");
    }

    private async Task NegotiateTlsAsync(CancellationToken cancellationToken)
    {
        var auth = await SendAsync("AUTH TLS", cancellationToken).ConfigureAwait(false);
        if (!auth.IsPositive)
            throw new IOException($"The FTP server does not support TLS: {Summarize(auth)}");

        var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _host }, cancellationToken)
            .ConfigureAwait(false);

        _stream = ssl;
        _reader.Dispose();
        _reader = new StreamReader(_stream, _encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        // PBSZ 0 then PROT P is the fixed incantation that puts the *data*
        // channel under TLS too. Without it the login is encrypted and the file
        // still crosses the network in the clear.
        await SendAsync("PBSZ 0", cancellationToken).ConfigureAwait(false);
        var prot = await SendAsync("PROT P", cancellationToken).ConfigureAwait(false);
        _protectData = prot.IsPositive;
    }

    // ------------------------------------------------------------ metadata --

    /// <summary>File size in bytes, or 0 when the server won't answer <c>SIZE</c>.</summary>
    public async Task<long> GetSizeAsync(string path, CancellationToken cancellationToken)
    {
        var reply = await SendAsync($"SIZE {path}", cancellationToken).ConfigureAwait(false);
        if (reply.Code != 213) return 0;

        string digits = reply.Text.Length > 3 ? reply.Text[3..].Trim() : string.Empty;
        return long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long size) ? size : 0;
    }

    public async Task<DateTimeOffset?> GetLastModifiedAsync(string path, CancellationToken cancellationToken)
    {
        var reply = await SendAsync($"MDTM {path}", cancellationToken).ConfigureAwait(false);
        if (reply.Code != 213) return null;

        string stamp = reply.Text.Length > 3 ? reply.Text[3..].Trim() : string.Empty;
        return DateTimeOffset.TryParseExact(stamp, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// Whether the server advertises restartable stream transfers, which is what
    /// decides between a segmented download and a single connection.
    /// </summary>
    /// <remarks>
    /// A server that doesn't implement <c>FEAT</c> at all is assumed to support
    /// <c>REST</c>: it predates the extension list, and restart has been in the
    /// base protocol since RFC 959. If that assumption is wrong the transfer
    /// still recovers — <see cref="OpenReadAsync"/> reports the refusal and the
    /// connection restarts from zero rather than writing misplaced bytes.
    /// </remarks>
    public async Task<bool> SupportsRestartAsync(CancellationToken cancellationToken)
    {
        var reply = await SendAsync("FEAT", cancellationToken).ConfigureAwait(false);
        if (reply.Code != 211) return true;

        return reply.Text.Contains("REST STREAM", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------ transfer --

    /// <summary>
    /// Opens a binary read of <paramref name="path"/> starting at
    /// <paramref name="offset"/>. The returned stream ends when the server closes
    /// the data connection; a caller that wants only part of the file simply
    /// stops reading and disposes this channel.
    /// </summary>
    /// <exception cref="FtpRestartNotSupportedException">
    /// <paramref name="offset"/> is non-zero and the server refused <c>REST</c>.
    /// </exception>
    public async Task<Stream> OpenReadAsync(string path, long offset, CancellationToken cancellationToken)
    {
        await OpenDataConnectionAsync(cancellationToken).ConfigureAwait(false);

        if (offset > 0)
        {
            var rest = await SendAsync($"REST {offset.ToString(CultureInfo.InvariantCulture)}", cancellationToken).ConfigureAwait(false);
            if (rest.Code != 350)
                throw new FtpRestartNotSupportedException($"The FTP server refused to restart at byte {offset}: {Summarize(rest)}");
        }

        var retr = await SendAsync($"RETR {path}", cancellationToken).ConfigureAwait(false);

        // 125/150 mean the data connection is live and the bytes are coming.
        // Anything else — 550 for a missing file, 425 for a data connection the
        // server could not open — means there will never be any.
        if (!retr.IsPreliminary)
            throw new IOException($"The FTP server refused the transfer: {Summarize(retr)}");

        return _dataStream!;
    }

    private async Task OpenDataConnectionAsync(CancellationToken cancellationToken)
    {
        (string host, int port) = await EnterPassiveModeAsync(cancellationToken).ConfigureAwait(false);

        var data = new TcpClient();
        using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectTimeout.CancelAfter(ConnectTimeoutMilliseconds);
        await data.ConnectAsync(host, port, connectTimeout.Token).ConfigureAwait(false);

        _data = data;
        Stream stream = data.GetStream();

        if (_secure && _protectData)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _host }, cancellationToken)
                .ConfigureAwait(false);
            stream = ssl;
        }

        _dataStream = stream;
    }

    private async Task<(string Host, int Port)> EnterPassiveModeAsync(CancellationToken cancellationToken)
    {
        // EPSV first over IPv6, where PASV's four-octet address simply cannot be
        // expressed. Everywhere else PASV goes first because it is the one every
        // server implements.
        bool preferExtended = _control.Client.RemoteEndPoint is System.Net.IPEndPoint endPoint
                              && endPoint.AddressFamily == AddressFamily.InterNetworkV6;

        if (preferExtended)
        {
            int? extended = await TryExtendedPassiveAsync(cancellationToken).ConfigureAwait(false);
            if (extended is int preferredPort) return (_host, preferredPort);
        }

        var pasv = await SendAsync("PASV", cancellationToken).ConfigureAwait(false);
        if (pasv.Code == 227)
        {
            var match = PassivePattern.Match(pasv.Text);
            if (match.Success)
            {
                int port = (int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture) << 8)
                           + int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture);

                // The advertised IP is deliberately ignored in favour of the host
                // the control connection already reached: a server behind NAT
                // routinely announces its private address here, and dialling that
                // is the classic "passive mode hangs" failure.
                return (_host, port);
            }
        }

        if (!preferExtended)
        {
            int? extended = await TryExtendedPassiveAsync(cancellationToken).ConfigureAwait(false);
            if (extended is int fallbackPort) return (_host, fallbackPort);
        }

        throw new IOException($"The FTP server would not open a passive data connection: {Summarize(pasv)}");
    }

    private async Task<int?> TryExtendedPassiveAsync(CancellationToken cancellationToken)
    {
        var epsv = await SendAsync("EPSV", cancellationToken).ConfigureAwait(false);
        if (epsv.Code != 229) return null;

        var match = ExtendedPassivePattern.Match(epsv.Text);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    // ------------------------------------------------------------ plumbing --

    private async Task<FtpReply> SendAsync(string command, CancellationToken cancellationToken)
    {
        byte[] bytes = _encoding.GetBytes(command + "\r\n");
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return await ReadReplyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one reply, joining the continuation lines of a multi-line block.
    /// A block opens with <c>NNN-</c> and closes with the same code followed by a
    /// space, so <c>FEAT</c>'s feature list arrives as a single searchable string.
    /// </summary>
    private async Task<FtpReply> ReadReplyAsync(CancellationToken cancellationToken)
    {
        string line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)
                      ?? throw new IOException("The FTP server closed the connection.");

        if (line.Length < 4 || !int.TryParse(line.AsSpan(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, out int code))
            throw new IOException($"Unexpected reply from the FTP server: {line}");

        if (line[3] != '-') return new FtpReply(code, line);

        string terminator = line[..3] + " ";
        var text = new StringBuilder(line);
        while (true)
        {
            string? next = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (next is null) break;

            text.Append('\n').Append(next);
            if (next.StartsWith(terminator, StringComparison.Ordinal)) break;
        }

        return new FtpReply(code, text.ToString());
    }

    /// <summary>Collapses a reply to one line so it can go in an exception message.</summary>
    private static string Summarize(FtpReply reply) =>
        reply.Text.Replace('\n', ' ').Replace('\r', ' ').Trim();

    /// <summary>
    /// Tears down both connections without a polite <c>QUIT</c>. A segment that
    /// has read the bytes it wanted is mid-transfer as far as the server is
    /// concerned, so there is no reply left to wait for that isn't an abort — and
    /// a blocking handshake here would stall pause and cancel.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        try { _dataStream?.Dispose(); } catch { /* best-effort */ }
        try { _data?.Dispose(); } catch { /* best-effort */ }
        try { _reader.Dispose(); } catch { /* best-effort */ }
        try { _stream.Dispose(); } catch { /* best-effort */ }
        try { _control.Dispose(); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }
}
