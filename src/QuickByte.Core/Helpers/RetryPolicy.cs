using System.Threading;

namespace QuickByte.Core.Helpers;

/// <summary>
/// Strategy-pattern retry executor with exponential backoff. Used by
/// connections to transparently recover from transient network failures.
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
            catch (Exception ex) when (attempt < maxRetries && ex is not OperationCanceledException)
            {
                attempt++;
                onRetry?.Invoke(attempt, ex);
                var delay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
