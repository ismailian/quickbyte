using System.Threading;
using QuickByte.Core.Models;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// Resolves metadata about a remote file (name, size, content type, range
/// support) before a download is created, so the "Add Download" dialog can
/// show the user what they're about to get.
/// </summary>
public interface IRemoteFileInfoProvider
{
    Task<RemoteFileInfo> GetFileInfoAsync(string url, CancellationToken cancellationToken = default);
}
