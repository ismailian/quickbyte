using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace QuickByte.Core.Commands;

/// <summary>
/// The channel between <c>qb.exe</c> and a running QuickByte: a named pipe, one
/// JSON request line in, one JSON response line out.
///
/// A second pipe rather than more traffic on the single-instance one, which is
/// one-way by design — a launch hands over its command line and exits. A command
/// has an answer to wait for.
///
/// Both ends are <see cref="PipeOptions.CurrentUserOnly"/>: the pipe is created
/// with an access list naming only this user, and the client checks the server is
/// owned by this user before it says anything. Another account on the machine
/// can neither send QuickByte a download nor impersonate QuickByte to <c>qb</c>.
/// </summary>
public static class CommandPipe
{
    /// <summary>Per user, because pipe names are machine-wide.</summary>
    public static string Name => $"QuickByte.Command.{Environment.UserName}";

    /// <summary>A request is a handful of links and switches; anything this long is not one.</summary>
    public const int MaxMessageChars = 64 * 1024;

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Sends <paramref name="request"/> and waits for the answer. Null when nothing
    /// is listening — QuickByte is not running — within <paramref name="connectTimeout"/>.
    /// </summary>
    public static async Task<CommandResponse?> SendAsync(
        CommandRequest request, TimeSpan connectTimeout, TimeSpan replyTimeout, string? pipeName = null)
    {
        using var client = new NamedPipeClientStream(".", pipeName ?? Name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var connect = new CancellationTokenSource(connectTimeout);
            await client.ConnectAsync(connect.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException)
        {
            return null;
        }

        using var reply = new CancellationTokenSource(replyTimeout);
        await WriteLineAsync(client, JsonSerializer.Serialize(request, Options), reply.Token).ConfigureAwait(false);
        string? line = await ReadLineAsync(client, reply.Token).ConfigureAwait(false);
        return line is null ? null : JsonSerializer.Deserialize<CommandResponse>(line, Options);
    }

    /// <summary>
    /// Answers requests until <paramref name="stopping"/> is cancelled, one client at
    /// a time — commands run on the UI thread anyway, so a queue of them is what
    /// they would be regardless. A malformed request is answered, not fatal.
    /// </summary>
    public static async Task ServeAsync(
        Func<CommandRequest, Task<CommandResponse>> handle, CancellationToken stopping, string? pipeName = null)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    pipeName ?? Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(stopping).ConfigureAwait(false);

                using var exchange = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                exchange.CancelAfter(TimeSpan.FromMinutes(5));

                CommandResponse response;
                string? line = await ReadLineAsync(server, exchange.Token).ConfigureAwait(false);
                CommandRequest? request = null;
                try
                {
                    if (line is not null) request = JsonSerializer.Deserialize<CommandRequest>(line, Options);
                }
                catch (JsonException)
                {
                    // Answered below as not understood.
                }

                response = request is null
                    ? CommandResponse.Fail("QuickByte did not understand that request. Is qb the same version as QuickByte?")
                    : await handle(request).ConfigureAwait(false);

                await WriteLineAsync(server, JsonSerializer.Serialize(response, Options), exchange.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A client that went away mid-exchange; the next one still gets an answer.
                if (stopping.IsCancellationRequested) break;
            }
        }
    }

    private static async Task WriteLineAsync(Stream stream, string json, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    /// <summary>One line, read a byte at a time so nothing after it is taken, and never longer than a request can be.</summary>
    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>(1024);
        var one = new byte[1];
        while (true)
        {
            int read = await stream.ReadAsync(one, token).ConfigureAwait(false);
            if (read == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(bytes.ToArray());
            if (bytes.Count >= MaxMessageChars * 4) throw new IOException("The message is too long.");
            bytes.Add(one[0]);
        }
    }
}
