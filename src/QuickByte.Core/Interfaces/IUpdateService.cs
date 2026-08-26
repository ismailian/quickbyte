using QuickByte.Core.Models;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// Checks whether a newer QuickByte has been published and fetches its
/// installer. Lives in Core, and therefore knows nothing about windows: it
/// returns a file path and lets the UI decide whether to run it, prompt
/// first, or throw it away.
/// </summary>
public interface IUpdateService
{
    /// <summary>The endpoint the manifest is read from, for display in an error or an About box.</summary>
    string ManifestUrl { get; }

    /// <summary>
    /// Fetches the release manifest and compares it against
    /// <paramref name="currentVersion"/> (the running build's product version).
    /// Throws on network or parse failure — a background caller swallows that,
    /// a manual one shows it, and the service shouldn't have to guess which.
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdateAsync(string currentVersion, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the installer described by <paramref name="manifest"/> to a
    /// temp folder and returns its full path, verifying the manifest's SHA-256
    /// when one is given. Reports progress on <paramref name="progress"/>.
    /// </summary>
    Task<string> DownloadInstallerAsync(
        UpdateManifest manifest,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes installers an earlier run downloaded and left in the temp folder,
    /// returning how many went. Setup can't delete the file it is running from
    /// and the app that fetched it has already exited, so a startup sweep is the
    /// only thing that ever collects them — call it once, unawaited, the way
    /// <see cref="IDownloadManager.CleanupOrphanedTempFoldersAsync"/> is called.
    /// Never throws: anything still locked is left for the next launch.
    /// </summary>
    Task<int> CleanupDownloadedInstallersAsync(CancellationToken cancellationToken = default);
}
