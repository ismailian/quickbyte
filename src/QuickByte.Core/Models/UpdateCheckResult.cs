namespace QuickByte.Core.Models;

/// <summary>
/// Outcome of one update check. Carries both versions rather than just a bool
/// so the caller can say "1.2.1 → 1.3.0" without re-reading the manifest, and
/// so the "you're up to date" path still has a number to show.
/// </summary>
public sealed class UpdateCheckResult
{
    private UpdateCheckResult(bool updateAvailable, string currentVersion, UpdateManifest? manifest)
    {
        UpdateAvailable = updateAvailable;
        CurrentVersion = currentVersion;
        Manifest = manifest;
    }

    /// <summary>True when <see cref="Manifest"/> describes a version newer than the running one.</summary>
    public bool UpdateAvailable { get; }

    /// <summary>The running build's version, as it was passed to the check.</summary>
    public string CurrentVersion { get; }

    /// <summary>
    /// The fetched release descriptor. Non-null whenever the endpoint answered
    /// with something usable — including when it describes the version already
    /// installed — and null only when the manifest was unusable.
    /// </summary>
    public UpdateManifest? Manifest { get; }

    /// <summary>Version offered by the endpoint, or the current one if there was nothing to read.</summary>
    public string LatestVersion => Manifest?.Version ?? CurrentVersion;

    public static UpdateCheckResult UpToDate(string currentVersion, UpdateManifest? manifest = null) =>
        new(false, currentVersion, manifest);

    public static UpdateCheckResult Available(string currentVersion, UpdateManifest manifest) =>
        new(true, currentVersion, manifest);
}
