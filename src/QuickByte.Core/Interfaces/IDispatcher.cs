namespace QuickByte.Core.Interfaces;

/// <summary>
/// Abstraction over "run this on the UI thread". QuickByte.Core has zero
/// reference to System.Windows.Forms; the UI project supplies a
/// SynchronizationContext-based implementation at composition-root time.
/// This is what keeps every window synchronized without any window knowing
/// about the others.
/// </summary>
public interface IDispatcher
{
    void Post(Action action);
}
