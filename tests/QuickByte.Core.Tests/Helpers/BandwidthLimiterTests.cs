using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;

namespace QuickByte.Core.Tests.Helpers;

/// <summary>
/// The token bucket behind the Speed Limiter tab, and the composite that stacks
/// a download's own cap on the queue's and the application's.
///
/// Two invariants matter more than the arithmetic: the unlimited case must cost
/// nothing, and unused allowance must find its way back — budget spent on bytes
/// that never moved is what makes a download settle well below its configured
/// rate.
/// </summary>
public sealed class BandwidthLimiterTests
{
    [Fact]
    public async Task Rate_of_zero_grants_everything_asked_for()
    {
        var limiter = new BandwidthLimiter(0);

        Assert.Equal(64 * 1024, await limiter.RequestAsync(64 * 1024, CancellationToken.None));
    }

    [Fact]
    public async Task A_rate_never_grants_more_than_was_asked_for()
    {
        var limiter = new BandwidthLimiter(10_000_000);

        int granted = await limiter.RequestAsync(4096, CancellationToken.None);

        Assert.InRange(granted, 1, 4096);
    }

    [Fact]
    public async Task A_rate_grants_at_least_one_byte_rather_than_spinning()
    {
        var limiter = new BandwidthLimiter(64);

        int granted = await limiter.RequestAsync(64 * 1024, CancellationToken.None);

        Assert.True(granted >= 1);
    }

    [Fact]
    public async Task Nothing_is_granted_for_a_request_of_nothing() =>
        Assert.Equal(0, await new BandwidthLimiter(1000).RequestAsync(0, CancellationToken.None));

    [Fact]
    public void Negative_rates_are_read_as_unlimited() =>
        Assert.Equal(0, new BandwidthLimiter(-5).BytesPerSecond);

    [Fact]
    public async Task The_rate_is_read_fresh_on_every_request()
    {
        // This is what makes the Speed Limiter tab apply to a transfer already in
        // flight rather than only to the next one.
        var limiter = new BandwidthLimiter(0);
        Assert.Equal(8192, await limiter.RequestAsync(8192, CancellationToken.None));

        limiter.BytesPerSecond = 128;
        Assert.True(await limiter.RequestAsync(8192, CancellationToken.None) < 8192);
    }

    [Fact]
    public async Task Returned_allowance_can_be_spent_again()
    {
        var limiter = new BandwidthLimiter(100_000);
        int first = await limiter.RequestAsync(50_000, CancellationToken.None);

        limiter.Return(first);

        // Without the refund the bucket would still be empty of what it just
        // handed out for bytes that never moved.
        Assert.True(await limiter.RequestAsync(first, CancellationToken.None) >= first);
    }

    [Fact]
    public async Task A_cancelled_request_does_not_wait_out_its_backoff()
    {
        // One byte a second: the bucket is dry for a very long time.
        var limiter = new BandwidthLimiter(1);
        await limiter.RequestAsync(1024, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await limiter.RequestAsync(1024, cts.Token));
    }

    [Fact]
    public async Task Unlimited_grants_whatever_it_is_asked_for()
    {
        Assert.Equal(
            1234,
            await UnlimitedBandwidthLimiter.Instance.RequestAsync(1234, CancellationToken.None));

        // And has nothing to do with a refund.
        UnlimitedBandwidthLimiter.Instance.Return(1234);
    }

    // ------------------------------------------------------------ composite --

    [Fact]
    public async Task Composite_grants_only_what_the_tightest_limiter_allows()
    {
        var loose = new RecordingLimiter(grant: 64 * 1024);
        var tight = new RecordingLimiter(grant: 8 * 1024);
        var composite = new CompositeBandwidthLimiter(loose, tight);

        int granted = await composite.RequestAsync(64 * 1024, CancellationToken.None);

        Assert.Equal(8 * 1024, granted);
    }

    [Fact]
    public async Task Composite_refunds_the_looser_limiter_the_difference()
    {
        // Spending 64 KB of the global budget to move 8 KB would drag the global
        // limit down to the per-download one.
        var global = new RecordingLimiter(grant: 64 * 1024);
        var perDownload = new RecordingLimiter(grant: 8 * 1024);
        var composite = new CompositeBandwidthLimiter(global, perDownload);

        await composite.RequestAsync(64 * 1024, CancellationToken.None);

        Assert.Equal(64 * 1024 - 8 * 1024, global.Returned);
        Assert.Equal(0, perDownload.Returned);
    }

    [Fact]
    public async Task Composite_asks_each_limiter_only_for_what_is_still_on_the_table()
    {
        var first = new RecordingLimiter(grant: 4096);
        var second = new RecordingLimiter(grant: 64 * 1024);
        var composite = new CompositeBandwidthLimiter(first, second);

        await composite.RequestAsync(64 * 1024, CancellationToken.None);

        Assert.Equal(64 * 1024, first.LastRequested);
        Assert.Equal(4096, second.LastRequested);
    }

    [Fact]
    public void Composite_returns_unused_allowance_to_every_limiter()
    {
        var first = new RecordingLimiter(grant: 1);
        var second = new RecordingLimiter(grant: 1);

        new CompositeBandwidthLimiter(first, second).Return(500);

        Assert.Equal(500, first.Returned);
        Assert.Equal(500, second.Returned);
    }

    [Fact]
    public async Task Composite_of_nothing_grants_everything() =>
        Assert.Equal(4096, await new CompositeBandwidthLimiter().RequestAsync(4096, CancellationToken.None));

    private sealed class RecordingLimiter : IBandwidthLimiter
    {
        private readonly int _grant;

        public RecordingLimiter(int grant) => _grant = grant;

        public int LastRequested { get; private set; }
        public int Returned { get; private set; }

        public ValueTask<int> RequestAsync(int desiredBytes, CancellationToken cancellationToken)
        {
            LastRequested = desiredBytes;
            return ValueTask.FromResult(Math.Min(desiredBytes, _grant));
        }

        public void Return(int unusedBytes) => Returned += unusedBytes;
    }
}
