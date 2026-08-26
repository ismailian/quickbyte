using System.Threading;

namespace QuickByte.UI.Tests;

/// <summary>
/// The one place Core's events cross onto the UI thread.
///
/// Progress arrives on a timer callback, downloads finish on thread-pool
/// threads, the browser bridge accepts on its own — and every one of those ends
/// up in a form through here. What matters is that it <em>posts</em>: an
/// implementation that invoked the action inline would look identical in every
/// screenshot and would be touching controls from a pool thread.
/// </summary>
public sealed class AppDispatcherTests
{
    [Fact]
    public void An_action_is_posted_to_the_context_rather_than_run_on_the_spot()
    {
        var context = new RecordingContext();
        bool ran = false;

        new AppDispatcher(context).Post(() => ran = true);

        Assert.Single(context.Posted);
        Assert.False(ran, "the action ran on the calling thread instead of being marshaled");
    }

    [Fact]
    public void The_posted_callback_runs_the_action_it_was_given()
    {
        var context = new RecordingContext();
        int calls = 0;

        new AppDispatcher(context).Post(() => calls++);
        context.RunAll();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Actions_reach_the_context_in_the_order_they_were_posted()
    {
        // Progress, then status, then the list change: an event stream that
        // arrives out of order is one that draws a finished download as running.
        var context = new RecordingContext();
        var order = new List<int>();
        var dispatcher = new AppDispatcher(context);

        dispatcher.Post(() => order.Add(1));
        dispatcher.Post(() => order.Add(2));
        dispatcher.Post(() => order.Add(3));
        context.RunAll();

        Assert.Equal(new[] { 1, 2, 3 }, order);
    }

    /// <summary>A context that captures what was posted instead of running it.</summary>
    private sealed class RecordingContext : SynchronizationContext
    {
        public List<(SendOrPostCallback Callback, object? State)> Posted { get; } = new();

        public override void Post(SendOrPostCallback d, object? state) => Posted.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) =>
            throw new InvalidOperationException("the dispatcher must not block the raising thread");

        public void RunAll()
        {
            foreach (var (callback, state) in Posted) callback(state);
        }
    }
}
