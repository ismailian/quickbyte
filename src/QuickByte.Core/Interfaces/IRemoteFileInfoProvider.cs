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
    /// <param name="options">
    /// Credentials and headers to probe with. The probe has to speak to the
    /// server exactly the way the connections will, or it resolves the size of
    /// something the download then never sees — a login page, or a 401 body.
    /// Pass <see cref="RequestOptions.None"/> for an anonymous, header-free
    /// request.
    /// </param>
    Task<RemoteFileInfo> GetFileInfoAsync(string url, RequestOptions? options = null, CancellationToken cancellationToken = default);
}
