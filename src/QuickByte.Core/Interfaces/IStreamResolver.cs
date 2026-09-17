using System.Threading;
using QuickByte.Core.Models;
using QuickByte.Core.Streaming;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// Reads a streaming video's playlist or manifest and describes the qualities it
/// can be downloaded in, so the Add window can offer a choice instead of saving
/// the playlist itself — a text file of addresses, which is what a download
/// manager used to hand back for every link a video player had loaded.
/// </summary>
public interface IStreamResolver
{
    /// <param name="options">The same credentials and headers the download will use.</param>
    /// <exception cref="Exceptions.StreamNotSupportedException">A live or DRM-protected stream.</exception>
    /// <exception cref="Exceptions.AuthenticationRequiredException">The playlist needs a login.</exception>
    /// <exception cref="FormatException">Not a playlist this can read.</exception>
    Task<StreamDescription> DescribeAsync(string url, RequestOptions? options, CancellationToken cancellationToken);
}
