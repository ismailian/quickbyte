namespace QuickByte.Core.Models;

/// <summary>
/// A new address for a download that already exists — what "Refresh Download
/// Address" hands to <see cref="Interfaces.IDownloadManager.ChangeAddress"/>.
///
/// It exists because the address is frequently the only thing wrong with a
/// failed download. Signed links (S3, Google Drive, most CDN download buttons)
/// expire, and a download paused overnight comes back to a <c>403</c> while
/// every byte it already fetched is sitting on disk. The file is the same file;
/// only the permission to ask for it ran out.
/// </summary>
/// <param name="Url">The new address, with any <c>user:pass@</c> already lifted out.</param>
/// <param name="FileInfo">
/// What a probe of the new address resolved. The caller has already judged it
/// against the download with <see cref="Helpers.AddressRefresh.Judge"/>; this is
/// what becomes the download's size, range support and version.
/// </param>
public sealed record AddressChange(string Url, RemoteFileInfo FileInfo)
{
    /// <summary>A login to replace the download's own; null keeps the one it has.</summary>
    public DownloadCredentials? Credentials { get; init; }

    /// <summary>
    /// Request headers to replace the download's own; null keeps them. A link
    /// re-captured from the browser brings a fresh cookie and referrer, and the
    /// old session cookie is usually part of why the old link stopped working.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>
    /// Throw away what is on disk and start the file over. Only ever true when
    /// the user agreed to it — see <see cref="Helpers.AddressVerdict"/>.
    /// </summary>
    public bool DiscardProgress { get; init; }
}
