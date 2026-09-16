using QuickByte.Core.Models;

namespace QuickByte.Core.Helpers;

/// <summary>What a new address turned out to point at, compared with the download it would continue.</summary>
public enum AddressVerdict
{
    /// <summary>The same file. The download carries on from the bytes it has.</summary>
    SameFile,

    /// <summary>
    /// A file of a different length. Not this download under a new name — the
    /// chunks cannot be continued, and the file name, category and size the user
    /// chose all describe something else. It has to be added as a download of
    /// its own.
    /// </summary>
    DifferentSize,

    /// <summary>
    /// The same length, but the server that issued the download's version now
    /// describes a different one. Continuing would merge two versions into one
    /// file; starting over is the only safe way to use the address.
    /// </summary>
    NewVersion,

    /// <summary>
    /// Nothing to compare: one side never said how big the file is. Harmless when
    /// nothing has been downloaded yet, and the user's call when something has.
    /// </summary>
    Unverifiable
}

/// <summary>
/// The rules behind "Refresh Download Address": whether a new link may carry on
/// a download that already has bytes on disk, and whether a link the browser
/// just handed over is the one a refresh is waiting for.
///
/// Kept in Core, beside <see cref="FileVersionCheck"/> whose verdict it builds
/// on, because the cost of getting it wrong is a corrupt file rather than a
/// wrong-looking window.
/// </summary>
public static class AddressRefresh
{
    public static AddressVerdict Judge(DownloadItem item, RemoteFileInfo probed)
    {
        if (item.TotalBytes > 0 && probed.HasKnownSize && item.TotalBytes != probed.ContentLength)
            return AddressVerdict.DifferentSize;

        if (item.TotalBytes <= 0 || !probed.HasKnownSize)
            return HasProgress(item) ? AddressVerdict.Unverifiable : AddressVerdict.SameFile;

        // Same length. Only a validator from the host that issued the download's
        // own can say otherwise; a mirror's different ETag for the identical file
        // is Unknown here, which is exactly what makes switching mirrors work.
        var version = FileVersionCheck.Compare(item.TotalBytes, item.RemoteVersion, probed.ContentLength, probed.Version);
        return version == FileVersionVerdict.Changed && HasProgress(item)
            ? AddressVerdict.NewVersion
            : AddressVerdict.SameFile;
    }

    /// <summary>
    /// Whether there are bytes on disk a new address could cost. A cancelled
    /// download has none whatever its counter says: Stop deleted its chunks.
    /// </summary>
    public static bool HasProgress(DownloadItem item) =>
        item.DownloadedBytes > 0 && item.Status != Enums.DownloadStatus.Cancelled;

    /// <summary>
    /// Whether a link the browser extension just handed over is plausibly the one
    /// a refresh for <paramref name="item"/> is waiting for — the user went back
    /// to the page and clicked the download again.
    ///
    /// Deliberately conservative: a capture that does not match opens the Add
    /// window as usual, and one that is wrongly claimed is a download the user
    /// asked for that never appears. So a known size must match exactly, and only
    /// when either side has no size does the file name decide.
    /// </summary>
    public static bool MatchesCapture(DownloadItem item, CapturedDownload captured)
    {
        if (!DownloadActions.CanRefreshAddress(item)) return false;

        if (item.TotalBytes > 0 && captured.TotalBytes > 0)
            return item.TotalBytes == captured.TotalBytes;

        if (string.IsNullOrWhiteSpace(captured.FileName)) return false;

        return string.Equals(
            FileNameHelper.SanitizeFileName(captured.FileName), item.FileName, StringComparison.OrdinalIgnoreCase);
    }
}
