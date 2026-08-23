using System.IO.Pipes;
using System.Threading;

namespace QuickByte.UI;

/// <summary>
/// Keeps QuickByte to one running copy per user, the way a download manager is
/// expected to behave: the app owns a persistent download list and a temp-folder
/// tree, and two processes writing <c>downloads.json</c> would quietly clobber
/// each other's state.
///
/// A named <see cref="Mutex"/> decides who wins; a named pipe gives the loser
/// somewhere to hand its command line before it exits, so launching QuickByte
/// with a URL while it is already running adds the download to the window you
/// already have instead of starting a rival process.
///
/// <see cref="SecondInstanceStarted"/> is raised on a thread-pool thread —
/// <see cref="Program"/> marshals it through <see cref="AppDispatcher"/> before
/// it reaches a form, matching how Core's events are handled.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    // "Local\" scopes the mutex to the logon session, which is what we want:
    // two users on the same machine each get their own QuickByte.
    private const string MutexName = @"Local\QuickByte.SingleInstance";
    private const int HandoffTimeoutMilliseconds = 3000;

    // Named pipes are machine-wide even when the mutex is not, so the user name
    // has to be part of the pipe name for the two to agree on scope.
    private static string PipeName => $"QuickByte.SingleInstance.{Environment.UserName}";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Raised with the payload sent by a second launch (may be empty).</summary>
    public event EventHandler<string>? SecondInstanceStarted;

    private SingleInstance(Mutex mutex)
    {
        _mutex = mutex;
        _ = Task.Run(() => ListenAsync(_cts.Token));
    }

    /// <summary>
    /// Claims ownership for this process, or returns <c>null</c> if another
    /// instance already holds it. The mutex is deliberately created unowned:
    /// existence is the signal, so there is no ownership to release on the
    /// wrong thread and nothing to abandon if the process is killed.
    /// </summary>
    public static SingleInstance? Acquire()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName, out bool createdNew);
        if (createdNew) return new SingleInstance(mutex);

        mutex.Dispose();
        return null;
    }

    /// <summary>
    /// Hands <paramref name="payload"/> to the instance that already owns the
    /// mutex. Returns false if nobody answered — the owner may be shutting down,
    /// in which case the caller has nothing useful left to do but exit anyway.
    /// </summary>
    public static bool SendToRunningInstance(string payload)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(HandoffTimeoutMilliseconds);

            using var writer = new StreamWriter(client);
            writer.Write(payload);
            writer.Flush();
            return true;
        }
        catch
        {
            // Timed out, or the owner tore the pipe down mid-handshake. Either
            // way this process is about to exit; a failed hand-off is not worth
            // a dialog.
            return false;
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server);
                string payload = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

                SecondInstanceStarted?.Invoke(this, payload.Trim());
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // A half-open connection or a malformed payload must not take the
                // listener down — the next launch still deserves to be answered.
            }
        }
    }

    /// <summary>
    /// Extracts the first absolute download URL from a command line, ignoring
    /// switches and anything that is not a link. Used both to decide what a
    /// second launch should hand over and to validate what arrives.
    /// </summary>
    /// <remarks>
    /// The scheme list is the same one the Add dialog accepts, FTP included —
    /// an ftp:// link handed over by the shell has to reach the dialog rather
    /// than being silently dropped as "not a URL".
    /// </remarks>
    public static string? FindUrl(IEnumerable<string> arguments) =>
        arguments.Select(argument => argument.Trim().Trim('"'))
                 .FirstOrDefault(argument =>
                     Uri.TryCreate(argument, UriKind.Absolute, out var uri) &&
                     uri.Scheme is "http" or "https" or "ftp" or "ftps");

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _mutex.Dispose();
    }
}
