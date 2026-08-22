using System.Text.Json.Serialization;

namespace QuickByte.Core.Models;

/// <summary>
/// The release descriptor QuickByte fetches from its update endpoint. Exists as
/// a separate model rather than being read straight off a <c>JsonDocument</c>
/// because everything downstream — the version comparison, the installer
/// download, the hash check — needs the same validated shape, and because the
/// document is written by hand on the release side where a typo is easy.
///
/// The JSON is deserialized case-insensitively, so <c>version</c> and
/// <c>Version</c> both bind; the names below are the canonical ones.
/// </summary>
public sealed class UpdateManifest
{
    /// <summary>Product version of the release, e.g. "1.3.0" (a leading "v" is tolerated).</summary>
    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    /// <summary>Direct HTTPS link to the installer executable.</summary>
    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>Free-text "what's new" shown in the update window. Optional.</summary>
    [JsonPropertyName("releaseNotes")]
    public string? ReleaseNotes { get; set; }

    /// <summary>When the release was published. Optional; only ever displayed.</summary>
    [JsonPropertyName("releaseDate")]
    public DateTimeOffset? ReleaseDate { get; set; }

    /// <summary>
    /// Installer size in bytes. Optional — the download reports real progress
    /// from Content-Length either way; this is only so the window can say how
    /// big the download will be before it starts.
    /// </summary>
    [JsonPropertyName("fileSizeBytes")]
    public long FileSizeBytes { get; set; }

    /// <summary>
    /// Hex SHA-256 of the installer. Optional, but strongly recommended: this is
    /// the one thing standing between a hijacked download and an executable the
    /// app runs on the user's behalf. When present it is verified before the
    /// file is handed back, and a mismatch deletes it.
    /// </summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    /// <summary>True when the manifest carries enough to act on.</summary>
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(Version) && !string.IsNullOrWhiteSpace(DownloadUrl);
}
