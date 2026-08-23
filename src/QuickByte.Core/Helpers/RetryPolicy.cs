using System.Threading;
using QuickByte.Core.Exceptions;

namespace QuickByte.Core.Helpers;

/// <summary>
/// Strategy-pattern retry executor with exponential backoff. Used by
/// connections to transparently recover from transient network failures.
///
/// Two failures are deliberately *not* transient and end the loop immediately:
/// cancellation, and <see cref="AuthenticationRequiredException"/>. A wrong
/// password will be just as wrong on the fourth attempt, and retrying it costs
/// the user the real error message — buried under a backoff delay — while some
/// servers count the repeats towards a lockout.
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
        exception is not OperationCanceledException and not AuthenticationRequiredException;
}
