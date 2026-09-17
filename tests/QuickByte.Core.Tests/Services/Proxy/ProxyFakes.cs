using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace QuickByte.Core.Tests.Services.Proxy;

/// <summary>
/// A working SOCKS5 proxy on loopback. It connects every request to 127.0.0.1 at
/// the requested port — so a test can ask for <c>files.test</c>, a name no resolver
/// knows, and prove the name travelled to the proxy rather than being looked up
/// here — and records what it was asked for and with which login.
/// </summary>
internal sealed class FakeSocks5Proxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly (string User, string Password)? _required;

    public FakeSocks5Proxy((string User, string Password)? requiredLogin = null)
    {
        _required = requiredLogin;
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ConcurrentQueue<(string Host, int Port)> Requests { get; } = new();

    public ConcurrentQueue<string> Logins { get; } = new();

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { return; }
            _ = ServeAsync(client);
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var s = client.GetStream();
                var head = await Read(s, 2);
                var methods = await Read(s, head[1]);

                if (_required is { } login)
                {
                    if (!methods.Contains((byte)2)) { await s.WriteAsync(new byte[] { 5, 0xFF }); return; }
                    await s.WriteAsync(new byte[] { 5, 2 });
                    await Read(s, 1);
                    string user = Encoding.UTF8.GetString(await Read(s, (await Read(s, 1))[0]));
                    string password = Encoding.UTF8.GetString(await Read(s, (await Read(s, 1))[0]));
                    Logins.Enqueue($"{user}:{password}");
                    bool ok = user == login.User && password == login.Password;
                    await s.WriteAsync(new byte[] { 1, ok ? (byte)0 : (byte)1 });
                    if (!ok) return;
                }
                else
                {
                    await s.WriteAsync(new byte[] { 5, 0 });
                }

                var request = await Read(s, 4);
                string host = request[3] switch
                {
                    1 => new IPAddress(await Read(s, 4)).ToString(),
                    4 => new IPAddress(await Read(s, 16)).ToString(),
                    _ => Encoding.ASCII.GetString(await Read(s, (await Read(s, 1))[0]))
                };
                var portBytes = await Read(s, 2);
                int port = portBytes[0] << 8 | portBytes[1];
                Requests.Enqueue((host, port));

                using var target = new TcpClient();
                try { await target.ConnectAsync(IPAddress.Loopback, port); }
                catch
                {
                    await s.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 });
                    return;
                }
                await s.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 0 });

                var t = target.GetStream();
                await Task.WhenAny(s.CopyToAsync(t), t.CopyToAsync(s));
            }
            catch
            {
                // A client that hung up mid-handshake.
            }
        }
    }

    private static async Task<byte[]> Read(Stream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}

/// <summary>
/// One connection, scripted: whatever arrives is recorded, and <paramref name="respond"/>
/// decides what goes back — the way a proxy's handshake is checked byte for byte.
/// </summary>
internal sealed class ScriptedServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public ScriptedServer(Func<NetworkStream, Task> script)
    {
        _listener.Start();
        Completion = Task.Run(async () =>
        {
            using var client = await _listener.AcceptTcpClientAsync();
            await script(client.GetStream());
        });
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public Task Completion { get; }

    public static async Task<byte[]> Read(Stream stream, int count)
    {
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    public static async Task<string> ReadHead(Stream stream)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (!(bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n'))
        {
            if (await stream.ReadAsync(one) == 0) break;
            bytes.Add(one[0]);
        }
        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    public void Dispose() => _listener.Stop();
}

/// <summary>An HTTP server that answers every request with the same small body.</summary>
internal sealed class TinyHttpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public TinyHttpServer(string body)
    {
        _listener.Start();
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch { return; }

                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            var s = client.GetStream();
                            await ScriptedServer.ReadHead(s);
                            byte[] content = Encoding.UTF8.GetBytes(body);
                            string head = $"HTTP/1.1 200 OK\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n";
                            await s.WriteAsync(Encoding.ASCII.GetBytes(head));
                            await s.WriteAsync(content);
                        }
                        catch { /* client gone */ }
                    }
                });
            }
        });
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}

/// <summary>
/// Just enough of an FTP server to log in and hand over one file by passive mode:
/// the conversation a download has, with nothing else.
/// </summary>
internal sealed class TinyFtpServer : IDisposable
{
    private readonly TcpListener _control = new(IPAddress.Loopback, 0);
    private readonly TcpListener _data = new(IPAddress.Loopback, 0);
    private readonly byte[] _file;

    public TinyFtpServer(byte[] file)
    {
        _file = file;
        _control.Start();
        _data.Start();
        _ = Task.Run(ServeAsync);
    }

    public int Port => ((IPEndPoint)_control.LocalEndpoint).Port;

    private async Task ServeAsync()
    {
        try
        {
            using var client = await _control.AcceptTcpClientAsync();
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            async Task Say(string line) => await stream.WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"));

            await Say("220 ready");
            while (await reader.ReadLineAsync() is { } command)
            {
                string verb = command.Split(' ')[0].ToUpperInvariant();
                switch (verb)
                {
                    case "USER": await Say("331 password please"); break;
                    case "PASS": await Say("230 logged in"); break;
                    case "TYPE": await Say("200 binary"); break;
                    case "PASV":
                        int port = ((IPEndPoint)_data.LocalEndpoint).Port;
                        await Say($"227 Entering Passive Mode (10,0,0,9,{port >> 8},{port & 0xFF})");
                        break;
                    case "RETR":
                        await Say("150 here it comes");
                        using (var data = await _data.AcceptTcpClientAsync())
                            await data.GetStream().WriteAsync(_file);
                        await Say("226 done");
                        break;
                    default: await Say("502 not here"); break;
                }
            }
        }
        catch
        {
            // The client closed the control connection.
        }
    }

    public void Dispose()
    {
        _control.Stop();
        _data.Stop();
    }
}
