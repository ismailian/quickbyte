using QuickByte.Core.Events;
using QuickByte.Core.Models;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// Orchestrates a single download end-to-end: fetches file info if needed,
/// drives the connection pool, hands off to the file merger, and exposes
/// the events both the main window and the download details window
/// subscribe to for real-time, synchronized feedback.
/// </summary>
public interface IDownloadService : IDisposable
{
    DownloadItem Item { get; }

    event EventHandler<DownloadProgressEventArgs>? ProgressChanged;
    event EventHandler<DownloadStatusChangedEventArgs>? StatusChanged;
    event EventHandler<ConnectionsSnapshotEventArgs>? ConnectionsChanged;

    Task StartAsync();
    void Pause();
    Task ResumeAsync();
    void Stop();
    Task RetryAsync();

    /// <summary>
    /// Points the download at a new address, keeping the bytes on disk unless
    /// <see cref="AddressChange.DiscardProgress"/> says otherwise. Returns false,
    /// and changes nothing, when the download is running or finished — see
    /// <see cref="Helpers.DownloadActions.CanRefreshAddress"/>.
    /// </summary>
    bool ChangeAddress(AddressChange change);
}
