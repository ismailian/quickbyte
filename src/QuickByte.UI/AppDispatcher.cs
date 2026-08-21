using System.Threading;
using QuickByte.Core.Interfaces;

namespace QuickByte.UI;

/// <summary>
/// WinForms implementation of <see cref="IDispatcher"/>. Captures the UI
/// thread's SynchronizationContext at startup and marshals every Core event
/// onto it, so Core never needs a reference to System.Windows.Forms and
/// every subscribing window receives updates safely regardless of which
/// background thread raised them.
/// </summary>
public sealed class AppDispatcher : IDispatcher
{
    private readonly SynchronizationContext _context;

    public AppDispatcher(SynchronizationContext context)
    {
        _context = context;
    }

    public void Post(Action action) => _context.Post(_ => action(), null);
}
