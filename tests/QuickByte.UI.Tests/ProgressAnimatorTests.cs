using QuickByte.UI.Controls;

namespace QuickByte.UI.Tests;

/// <summary>
/// The easing behind every progress bar and progress cell in the app.
///
/// Samples land every ~100 ms and are painted at ~60 fps, so what is on screen
/// is never the number the engine reported — it is this, moving towards it.
/// The interesting behaviour is at the edges: a value that has arrived must
/// stop moving (or the timers never stop), and a download that was retried has
/// to snap back rather than animate backwards for a second and a half.
/// </summary>
public sealed class ProgressAnimatorTests
{
    private readonly ProgressAnimator<string> _animator = new();

    [Fact]
    public void A_value_seen_for_the_first_time_is_displayed_as_it_is()
    {
        // Nothing animates up from zero when a download is added at 40% —
        // that is a resumed download, not progress being made.
        _animator.SetTarget("a", 40);

        Assert.Equal(40, _animator.Displayed("a"));
    }

    [Fact]
    public void A_key_nobody_has_reported_is_at_zero()
    {
        Assert.Equal(0, _animator.Displayed("never seen"));
    }

    [Fact]
    public void A_new_target_is_approached_a_fraction_at_a_time()
    {
        _animator.SetTarget("a", 0);
        _animator.SetTarget("a", 100);

        _animator.Advance();

        // A frame covers Easing (0.22) of what is left, so the first one lands
        // well short of the target -- that is the whole point.
        Assert.Equal(100 * ProgressAnimation.Easing, _animator.Displayed("a"), 6);
        Assert.True(_animator.Displayed("a") < 100);
    }

    [Fact]
    public void Enough_frames_arrive_at_the_target_and_then_stop()
    {
        _animator.SetTarget("a", 0);
        _animator.SetTarget("a", 100);

        for (int frame = 0; frame < 200; frame++) _animator.Advance();

        Assert.Equal(100, _animator.Displayed("a"));
        // Nothing moved on the last frame, so the caller's timer can stop.
        Assert.Empty(_animator.Advance());
    }

    [Fact]
    public void Advance_names_only_the_keys_that_moved()
    {
        // The list drives InvalidateCell, one row at a time. A key that has
        // converged must not be in it, or every row repaints 60 times a second.
        _animator.SetTarget("still", 50);
        _animator.SetTarget("moving", 0);
        _animator.SetTarget("moving", 90);

        Assert.Equal(new[] { "moving" }, _animator.Advance());
    }

    [Fact]
    public void A_gap_too_small_to_see_snaps_shut()
    {
        _animator.SetTarget("a", 50);
        _animator.SetTarget("a", 50 + ProgressAnimation.SnapThreshold / 2);

        _animator.Advance();

        Assert.Equal(50 + ProgressAnimation.SnapThreshold / 2, _animator.Displayed("a"));
        Assert.Empty(_animator.Advance());
    }

    [Fact]
    public void A_retry_snaps_back_instead_of_animating_in_reverse()
    {
        // A download that was retried starts again from nothing. Easing down to
        // it would read as progress being lost over the next second.
        _animator.SetTarget("a", 80);
        _animator.SetTarget("a", 0);

        Assert.Equal(0, _animator.Displayed("a"));
    }

    [Fact]
    public void A_small_regression_is_eased_rather_than_snapped()
    {
        // Jitter between two samples, not a reset: 78 after 80 is the engine
        // recounting, and snapping on it would make the bar twitch.
        _animator.SetTarget("a", 80);
        _animator.SetTarget("a", 78);

        Assert.Equal(80, _animator.Displayed("a"));

        _animator.Advance();

        Assert.InRange(_animator.Displayed("a"), 78, 80);
    }

    [Fact]
    public void An_immediate_target_skips_the_animation()
    {
        _animator.SetTarget("a", 0);
        _animator.SetTarget("a", 100, immediate: true);

        Assert.Equal(100, _animator.Displayed("a"));
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(150, 100)]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    public void A_percentage_is_clamped_to_a_percentage(double given, double expected)
    {
        _animator.SetTarget("a", given);

        Assert.Equal(expected, _animator.Displayed("a"));
    }

    [Fact]
    public void A_removed_key_is_forgotten()
    {
        // Rows come and go -- a finished download that is cleared from the list
        // must not leave its entry behind for Advance to walk every frame.
        _animator.SetTarget("a", 40);
        _animator.Remove("a");

        Assert.Equal(0, _animator.Displayed("a"));
        Assert.Empty(_animator.Advance());
    }

    [Fact]
    public void Clear_forgets_everything()
    {
        _animator.SetTarget("a", 40);
        _animator.SetTarget("b", 60);
        _animator.Clear();

        Assert.Equal(0, _animator.Displayed("a"));
        Assert.Equal(0, _animator.Displayed("b"));
    }

    [Fact]
    public void Every_key_animates_on_its_own()
    {
        _animator.SetTarget("a", 0);
        _animator.SetTarget("b", 0);
        _animator.SetTarget("a", 100);
        _animator.SetTarget("b", 50);

        _animator.Advance();

        Assert.Equal(100 * ProgressAnimation.Easing, _animator.Displayed("a"), 6);
        Assert.Equal(50 * ProgressAnimation.Easing, _animator.Displayed("b"), 6);
    }

    [Fact]
    public void The_frame_interval_is_about_sixty_a_second()
    {
        // The sampling interval (100 ms, in Core) is not the frame rate; this
        // is. Raising it does not reduce work, it just makes the bars step.
        Assert.InRange(1000.0 / ProgressAnimation.FrameIntervalMilliseconds, 55, 65);
    }
}
