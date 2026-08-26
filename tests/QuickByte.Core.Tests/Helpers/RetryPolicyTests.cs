using QuickByte.Core.Exceptions;
using QuickByte.Core.Helpers;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// What the policy refuses to retry matters more than what it does. A wrong
/// password is just as wrong on the fourth attempt, and retrying it buries the
/// one error message the user could have acted on under a backoff delay — while
/// some servers count the repeats towards a lockout.
/// </summary>
public sealed class RetryPolicyTests
{
    private static readonly TimeSpan NoDelay = TimeSpan.Zero;

    [Fact]
    public async Task ExecuteAsync_returns_the_first_success_without_retrying()
    {
        int calls = 0;

        int result = await RetryPolicy.ExecuteAsync(
            (_, _) => { calls++; return Task.FromResult(7); },
            maxRetries: 3, NoDelay, onRetry: null, CancellationToken.None);

        Assert.Equal(7, result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExecuteAsync_retries_a_transient_failure_until_it_succeeds()
    {
        int calls = 0;

        int result = await RetryPolicy.ExecuteAsync(
            (_, _) =>
            {
                calls++;
                if (calls < 3) throw new IOException("the connection was reset");
                return Task.FromResult(42);
            },
            maxRetries: 5, NoDelay, onRetry: null, CancellationToken.None);

        Assert.Equal(42, result);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ExecuteAsync_gives_up_after_the_configured_number_of_retries()
    {
        int calls = 0;

        await Assert.ThrowsAsync<IOException>(() => RetryPolicy.ExecuteAsync<int>(
            (_, _) => { calls++; throw new IOException("still broken"); },
            maxRetries: 3, NoDelay, onRetry: null, CancellationToken.None));

        // The first attempt is not a retry: 1 + 3.
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task ExecuteAsync_does_not_retry_a_rejected_password()
    {
        int calls = 0;

        var thrown = await Assert.ThrowsAsync<AuthenticationRequiredException>(() =>
            RetryPolicy.ExecuteAsync<int>(
                (_, _) =>
                {
                    calls++;
                    throw new AuthenticationRequiredException("nope") { CredentialsWereSupplied = true };
                },
                maxRetries: 5, NoDelay, onRetry: null, CancellationToken.None));

        Assert.Equal(1, calls);
        Assert.True(thrown.CredentialsWereSupplied);
    }

    [Fact]
    public async Task ExecuteAsync_does_not_retry_a_cancellation()
    {
        int calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RetryPolicy.ExecuteAsync<int>(
            (_, _) => { calls++; throw new OperationCanceledException(); },
            maxRetries: 5, NoDelay, onRetry: null, CancellationToken.None));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExecuteAsync_stops_before_the_first_attempt_when_already_cancelled()
    {
        int calls = 0;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RetryPolicy.ExecuteAsync<int>(
            (_, _) => { calls++; return Task.FromResult(1); },
            maxRetries: 3, NoDelay, onRetry: null, cts.Token));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExecuteAsync_reports_each_retry_with_its_attempt_number_and_cause()
    {
        var reported = new List<(int Attempt, string Message)>();

        await Assert.ThrowsAsync<IOException>(() => RetryPolicy.ExecuteAsync<int>(
            (_, _) => throw new IOException("boom"),
            maxRetries: 2, NoDelay,
            onRetry: (attempt, ex) => reported.Add((attempt, ex.Message)),
            CancellationToken.None));

        // The connection surfaces these as its LastError and RetryCount.
        Assert.Equal(new[] { 1, 2 }, reported.Select(r => r.Attempt).ToArray());
        Assert.All(reported, r => Assert.Equal("boom", r.Message));
    }

    [Fact]
    public async Task ExecuteAsync_hands_the_action_the_attempt_number()
    {
        var seen = new List<int>();

        await RetryPolicy.ExecuteAsync(
            (attempt, _) =>
            {
                seen.Add(attempt);
                if (attempt < 2) throw new IOException("again");
                return Task.FromResult(0);
            },
            maxRetries: 5, NoDelay, onRetry: null, CancellationToken.None);

        Assert.Equal(new[] { 0, 1, 2 }, seen.ToArray());
    }
}
