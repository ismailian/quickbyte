using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using QuickByte.Core.Helpers;
using QuickByte.Core.Interfaces;
using QuickByte.Core.Models;

namespace QuickByte.Core.Services;

/// <summary>
/// Reads the release manifest from a fixed endpoint and, when it describes a
/// newer build, downloads that build's installer to a temp folder.
///
/// Deliberately does <em>not</em> go through the segmented download engine: an
/// update is not a user download. It must not appear in the list, must not
/// persist to downloads.json, must not compete for the concurrency gate and
/// must not be throttled by the user's speed limit — and a single-stream copy
/// of an installer is not worth eight sockets.
///
/// Running the installer is the caller's job. Core has no business deciding
/// when the application is allowed to exit.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    /// <summary>
    /// Where releases are announced. Hardcoded on purpose: an update endpoint
    /// that a user — or anything else able to write settings.json — could
    /// repoint is a way to make QuickByte download and run an arbitrary
    /// executable.
    /// </summary>
    public const string DefaultManifestUrl = "https://quickbyte-cdn.ismailaatif.com/releases/latest.json";

    private const int BufferSize = 81920;

    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10)
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly string _downloadFolder;

    /// <param name="manifestUrl">
    /// Overridable only so a release can be staged against a test endpoint; the
    /// app itself always takes the default.
    /// </param>
    /// <param name="downloadFolder">Where the installer is written. Defaults to %TEMP%/QuickByte/updates.</param>
    public UpdateService(string? manifestUrl = null, string? downloadFolder = null)
    {
        ManifestUrl = string.IsNullOrWhiteSpace(manifestUrl) ? DefaultManifestUrl : manifestUrl;
        _downloadFolder = downloadFolder ?? Path.Combine(Path.GetTempPath(), "QuickByte", "updates");
    }

    public string ManifestUrl { get; }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ManifestUrl);

        // A CDN happily serving yesterday's manifest is the one failure mode
        // that looks exactly like "no update available", so ask for a fresh one.
        request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true };

        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        UpdateManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The update manifest could not be read.", ex);
        }

        if (manifest is null || !manifest.IsUsable)
            return UpdateCheckResult.UpToDate(currentVersion);

        return ProductVersion.IsNewer(manifest.Version, currentVersion)
            ? UpdateCheckResult.Available(currentVersion, manifest)
            : UpdateCheckResult.UpToDate(currentVersion, manifest);
    }

    public async Task<string> DownloadInstallerAsync(
        UpdateManifest manifest,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var uri = ResolveInstallerUri(manifest);

        Directory.CreateDirectory(_downloadFolder);
        PurgeStaleInstallers();

        string targetPath = Path.Combine(_downloadFolder,
            $"QuickByte-{FileNameHelper.SanitizeFileName(manifest.Version)}-Setup.exe");

        // HttpClient.Timeout is a whole-request budget, and the shared client's
        // 30 s is sized for a manifest fetch, not a multi-megabyte installer.
        // ResponseHeadersRead keeps that budget on the headers only; the body is
        // bounded by this far longer leash instead.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(30));

        try
        {
            await CopyToFileAsync(uri, targetPath, manifest.FileSizeBytes, progress, timeout.Token).ConfigureAwait(false);
            VerifyHash(targetPath, manifest.Sha256);
        }
        catch
        {
            // A partial or mismatched installer is worse than none: left behind,
            // it is a plausible-looking executable sitting in a temp folder.
            TryDelete(targetPath);
            throw;
        }

        return targetPath;
    }

    /// <summary>
    /// Rejects anything that isn't a plain HTTPS URL. This is the file the app
    /// is about to hand to the shell, so "the manifest said so" is not on its
    /// own reason enough to fetch it over a channel that can be rewritten in
    /// transit.
    /// </summary>
    private static Uri ResolveInstallerUri(UpdateManifest manifest)
    {
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The update download link is not a valid HTTPS URL.");
        }

        return uri;
    }

    private static async Task CopyToFileAsync(
        Uri uri,
        string targetPath,
        long declaredSize,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        long total = response.Content.Headers.ContentLength ?? declaredSize;

        using var http = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var file = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        var speed = new SpeedCalculator(TimeSpan.FromSeconds(3));
        var buffer = new byte[BufferSize];
        long received = 0;
        var lastReport = DateTime.UtcNow;

        while (true)
        {
            int read = await http.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            speed.AddSample(received);

            // Throttled for the same reason FileMerger throttles its merge
            // reports: one report per buffer floods the UI thread it is trying
            // to inform. 100 ms matches the engine's own sampling interval, and
            // the window interpolates between samples anyway.
            var now = DateTime.UtcNow;
            if (progress is not null && (now - lastReport).TotalMilliseconds >= 100)
            {
                lastReport = now;
                progress.Report(new UpdateDownloadProgress
                {
                    BytesReceived = received,
                    TotalBytes = total,
                    SpeedBytesPerSecond = speed.GetSpeedBytesPerSecond()
                });
            }
        }

        // The throttle above can swallow the last sample, and a bar that stops
        // at 99.4% reads as a stall.
        progress?.Report(new UpdateDownloadProgress
        {
            BytesReceived = received,
            TotalBytes = total > 0 ? total : received,
            SpeedBytesPerSecond = speed.GetSpeedBytesPerSecond()
        });
    }

    private static void VerifyHash(string filePath, string? expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return;

        using var stream = File.OpenRead(filePath);
        string actual = Convert.ToHexString(SHA256.HashData(stream));

        if (!actual.Equals(expectedHex.Replace("-", string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The downloaded installer failed its integrity check and was discarded.");
    }

    /// <summary>
    /// Drops installers left by earlier updates. They are dead weight the moment
    /// a newer one is fetched, and %TEMP% is nobody's idea of a release archive.
    /// </summary>
    private void PurgeStaleInstallers()
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(_downloadFolder, "QuickByte-*-Setup.exe"))
                TryDelete(file);
        }
        catch { /* best-effort */ }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* best-effort */ }
    }
}
