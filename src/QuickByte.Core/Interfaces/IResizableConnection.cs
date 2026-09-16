namespace QuickByte.Core.Interfaces;

/// <summary>
/// A connection whose range can be shortened while it runs — what lets the pool
/// hand the back half of a slow segment to a connection that has finished its own
/// (dynamic segmentation, see <see cref="Services.ConnectionPoolManager"/>).
///
/// Separate from <see cref="IDownloadConnection"/> so a connection that cannot
/// honour it is simply never split, rather than split and wrong.
/// </summary>
public interface IResizableConnection : IDownloadConnection
{
    /// <summary>The last byte written, or reserved by a read already under way. A split begins after it.</summary>
    long CommittedEnd { get; }

    /// <summary>
    /// Ends this connection's range at <paramref name="newEnd"/>, if nothing it has
    /// written or reserved lies beyond it. Returns false and changes nothing otherwise.
    /// </summary>
    bool TryShrinkEnd(long newEnd);
}
