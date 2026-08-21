namespace QuickByte.UI.Controls;

/// <summary>Shared tuning for every animated progress surface in the app.</summary>
public static class ProgressAnimation
{
    /// <summary>Frame interval of the UI timers driving interpolation (~60 fps).</summary>
    public const int FrameIntervalMilliseconds = 16;

    /// <summary>Fraction of the remaining distance covered per frame.</summary>
    public const double Easing = 0.22;

    /// <summary>Below this gap a value snaps — sub-pixel motion isn't worth a repaint.</summary>
    public const double SnapThreshold = 0.05;
}

/// <summary>
/// Eases displayed progress values toward the last value the download engine
/// reported, keyed by whatever identifies the thing being drawn (download id,
/// connection index).
///
/// Progress samples land every ~100 ms; painting them raw makes the bars step
/// in visible jumps. Every UI frame each displayed value moves a fraction of
/// the way to its target, so progress that really arrives in discrete samples
/// reads as continuous motion. Values only move backwards on an explicit reset
/// or a large jump (a retry), never on ordinary jitter.
/// </summary>
public sealed class ProgressAnimator<TKey> where TKey : notnull
{
    private readonly Dictionary<TKey, Entry> _entries = new();

    private sealed class Entry
    {
        public double Displayed;
        public double Target;
    }

    public void SetTarget(TKey key, double percentage, bool immediate = false)
    {
        percentage = Math.Clamp(percentage, 0, 100);
        if (!_entries.TryGetValue(key, out var entry))
        {
            _entries[key] = new Entry { Displayed = percentage, Target = percentage };
            return;
        }

        entry.Target = percentage;
        // A large backwards jump means the download was reset/retried, not that
        // progress regressed — snap instead of animating in reverse.
        if (immediate || percentage < entry.Displayed - 5)
            entry.Displayed = percentage;
    }

    public double Displayed(TKey key) => _entries.TryGetValue(key, out var entry) ? entry.Displayed : 0;

    public void Remove(TKey key) => _entries.Remove(key);

    public void Clear() => _entries.Clear();

    /// <summary>
    /// Advances every value one frame. Returns the keys that actually moved so
    /// the caller can invalidate just those rows.
    /// </summary>
    public IReadOnlyList<TKey> Advance()
    {
        List<TKey>? moved = null;

        foreach (var (key, entry) in _entries)
        {
            double gap = entry.Target - entry.Displayed;
            if (Math.Abs(gap) <= ProgressAnimation.SnapThreshold)
            {
                if (entry.Displayed == entry.Target) continue;
                entry.Displayed = entry.Target;
            }
            else
            {
                entry.Displayed += gap * ProgressAnimation.Easing;
            }

            (moved ??= new List<TKey>()).Add(key);
        }

        return (IReadOnlyList<TKey>?)moved ?? Array.Empty<TKey>();
    }
}
