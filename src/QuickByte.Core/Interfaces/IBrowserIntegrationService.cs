using QuickByte.Core.Models;

namespace QuickByte.Core.Interfaces;

/// <summary>
/// The loopback bridge the browser extension talks to. It listens on
/// 127.0.0.1 and raises <see cref="DownloadCaptured"/> when the extension hands
/// over a link it intercepted, which the UI turns into a pre-filled Add Download
/// window.
///
/// A local socket rather than a Chrome native-messaging host because a native
/// host needs a registry key naming the extension's own ID — which only exists
/// once the extension is packed and published — plus a second executable to
/// install and keep in step with the app. The bridge needs neither, and the
/// pairing token below is what a native host would otherwise get for free from
/// that registry key.
/// </summary>
public interface IBrowserIntegrationService : IDisposable
{
    /// <summary>
    /// Raised on a thread-pool thread when the extension posts a download.
    /// Callers touching UI must marshal it, exactly as with Core's other events.
    /// </summary>
    event EventHandler<CapturedDownload>? DownloadCaptured;

    /// <summary>Raised when the listener starts, stops, or fails to bind, so a settings page can show live status.</summary>
    event EventHandler? StatusChanged;

    bool IsRunning { get; }

    /// <summary>The loopback port currently listened on, or the configured one when stopped.</summary>
    int Port { get; }

    /// <summary>
    /// The shared secret the extension must send as <c>X-QuickByte-Token</c>.
    /// Generated on first use and persisted with the rest of the settings.
    /// </summary>
    string Token { get; }

    /// <summary>Why the last start attempt failed (a port already in use, usually), or null.</summary>
    string? LastError { get; }

    /// <summary>Starts listening if the feature is enabled; a no-op when it already is.</summary>
    void Start();

    void Stop();

    /// <summary>
    /// Issues a new token and persists it, revoking whatever was paired before.
    /// Every browser has to be re-paired afterwards — which is the point.
    /// </summary>
    string RegenerateToken();
}
