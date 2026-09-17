using System.Threading;
using QuickByte.Core.Exceptions;

namespace QuickByte.Core.Helpers;

/// <summary>
/// Strategy-pattern retry executor with exponential backoff. Used by
/// connections to transparently recover from transient network failures.
///
/// Three failures are deliberately *not* transient and end the loop immediately:
/// cancellation, <see cref="AuthenticationRequiredException"/>, and
/// <see cref="RemoteFileChangedException"/>. A wrong password will be just as
/// wrong on the fourth attempt, and retrying it costs the user the real error
/// message — buried under a backoff delay — while some servers count the repeats
/// towards a lockout. A file that changed on the server is no more fixable by
/// this connection asking again: the chunks of every sibling belong to the old
/// version too, and the decision belongs to the download as a whole.
///
/// A full disk ends it too, however it is reported. Space does not come back in
/// the seconds a backoff waits, and every retry pulls bytes off the network only
/// to fail writing them.
/// </summary>
public static class RetryPolicy
{
    public static async Task<T> ExecuteAsync<T>(
        Func<int, CancellationToken, Task<T>> action,
        int maxRetries,
        TimeSpan baseDelay,
        Action<int, Exception>? onRetry,
        CancellationToken cancellationToken)
    {
        int attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action(attempt, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxRetries && IsTransient(ex))
            {
                attempt++;
                onRetry?.Invoke(attempt, ex);
                var delay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception exception) =>
        exception is not OperationCanceledException
            and not AuthenticationRequiredException
            and not RemoteFileChangedException
            and not InsufficientDiskSpaceException
            and not StreamNotSupportedException
        && !IsDiskFull(exception);

    // ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL as HRESULTs. Space does not come
    // back in the few seconds a backoff waits, and each retry re-reads bytes off
    // the network only to fail writing them again.
    private const int DiskFull = unchecked((int)0x80070070);
    private const int HandleDiskFull = unchecked((int)0x80070027);

    private static bool IsDiskFull(Exception exception) =>
        exception is IOException { HResult: DiskFull or HandleDiskFull };
}
