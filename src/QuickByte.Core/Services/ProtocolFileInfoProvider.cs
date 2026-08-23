using System.Threading;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;
using QuickByte.Core.Services.Ftp;

namespace QuickByte.Core.Services;

/// <summary>
/// Routes a metadata probe to the provider that speaks the URL's scheme, the
/// mirror image of <see cref="ProtocolConnectionFactory"/>. The two must agree:
/// a file probed over FTP and then fetched over HTTP would resolve one size and
/// download something else entirely.
/// </summary>
public sealed class ProtocolFileInfoProvider : IRemoteFileInfoProvider
{
    private readonly IRemoteFileInfoProvider _http;
    private readonly IRemoteFileInfoProvider _ftp;

    public ProtocolFileInfoProvider(IRemoteFileInfoProvider? http = null, IRemoteFileInfoProvider? ftp = null)
    {
        _http = http ?? new RemoteFileInfoProvider();
        _ftp = ftp ?? new FtpFileInfoProvider();
    }

    public Task<RemoteFileInfo> GetFileInfoAsync(string url, RequestOptions? options = null, CancellationToken cancellationToken = default)
    {
        var provider = FtpUrl.IsFtp(url) ? _ftp : _http;
        return provider.GetFileInfoAsync(url, options, cancellationToken);
    }
}
