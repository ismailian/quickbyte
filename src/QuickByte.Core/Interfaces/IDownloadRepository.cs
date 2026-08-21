using QuickByte.Core.Models;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// Persists the list of <see cref="DownloadItem"/> to disk (JSON) so
/// in-progress/completed/failed downloads survive an application restart.
/// </summary>
public interface IDownloadRepository
{
    List<DownloadItem> LoadAll();
    void SaveAll(IEnumerable<DownloadItem> items);
}
