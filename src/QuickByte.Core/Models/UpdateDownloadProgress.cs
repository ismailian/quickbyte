namespace QuickByte.Core.Models;

/// <summary>
/// One progress sample from an installer download, handed to the caller's
/// <see cref="IProgress{T}"/>. Separate from
/// <see cref="Events.DownloadProgressEventArgs"/> because an update is not a
/// <see cref="DownloadItem"/>: it never enters the list, never persists, and
/// has no connection pool behind it.
/// </summary>
public sealed class UpdateDownloadProgress
{
    public required long BytesReceived { get; init; }

    /// <summary>Total size, or 0 when the server didn't send a Content-Length.</summary>
    public required long TotalBytes { get; init; }

    public required double SpeedBytesPerSecond { get; init; }

    /// <summary>0-100, or null when the total size is unknown and there is nothing to be a fraction of.</summary>
    public double? Percentage =>
        TotalBytes > 0 ? Math.Clamp(BytesReceived * 100.0 / TotalBytes, 0, 100) : null;
}
