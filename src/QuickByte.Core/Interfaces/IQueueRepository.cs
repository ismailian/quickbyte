using QuickByte.Core.Models;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// Persists the list of <see cref="DownloadQueue"/> (queues.json), the way
/// <see cref="IDownloadRepository"/> persists downloads.
///
/// Unlike that one, this file has a second reader in another process: the
/// scheduler agent reads it to decide when to wake QuickByte up. That is why
/// <see cref="TryLoadAll"/> reports failure instead of quietly returning an
/// empty list — "the file could not be read this second" and "there are no
/// queues" mean opposite things to a watcher that exits when it has nothing
/// left to watch.
/// </summary>
public interface IQueueRepository
{
    /// <summary>Loads every queue, or an empty list if there are none or the file is unreadable.</summary>
    List<DownloadQueue> LoadAll();

    /// <summary>
    /// Loads every queue, distinguishing "read it, there are none" (true, empty)
    /// from "could not read it" (false).
    /// </summary>
    bool TryLoadAll(out List<DownloadQueue> queues);

    void SaveAll(IEnumerable<DownloadQueue> queues);
}
