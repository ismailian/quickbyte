using System.Threading;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services.Ftp;

/// <summary>
/// The <c>ftp://</c> half of <see cref="IRemoteFileInfoProvider"/>: logs in,
/// asks <c>SIZE</c> for the length and <c>FEAT</c> for restart support, and
/// hands back the same <see cref="RemoteFileInfo"/> shape the HTTP provider
/// produces so nothing downstream has to know which protocol answered.
///
/// <see cref="RemoteFileInfo.SupportsRangeRequests"/> means "the server will
/// honour <c>REST</c>" here rather than "it sends Accept-Ranges", but it decides
/// the same thing either way: whether the pool splits the file across
/// connections or falls back to one.
/// </summary>
public sealed class FtpFileInfoProvider : IRemoteFileInfoProvider
{
    public async Task<RemoteFileInfo> GetFileInfoAsync(string url, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var uri = new Uri(url);
        string path = FtpUrl.PathOf(uri);

        var info = new RemoteFileInfo
        {
            FinalUrl = url,
            FileName = FtpUrl.FileNameOf(uri),

            // FTP has no Content-Type. The category icons key off the extension
            // anyway, so a generic type here costs nothing.
            ContentType = "application/octet-stream"
        };

        await using var channel = await FtpControlChannel
            .ConnectAsync(uri, options?.Credentials, cancellationToken)
            .ConfigureAwait(false);

        info.ContentLength = await channel.GetSizeAsync(path, cancellationToken).ConfigureAwait(false);
        info.LastModified = await channel.GetLastModifiedAsync(path, cancellationToken).ConfigureAwait(false);

        // Restart is only worth asking about when the size is known: without a
        // length there is nothing to split, and the pool drops to one connection
        // regardless.
        info.SupportsRangeRequests = info.HasKnownSize
            && await channel.SupportsRestartAsync(cancellationToken).ConfigureAwait(false);

        return info;
    }
}
