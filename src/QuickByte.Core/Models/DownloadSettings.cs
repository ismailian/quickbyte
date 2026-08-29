using System.Text.Json.Serialization;
using QuickByte.Core.Helpers;

namespace QuickByte.Core.Models;

/// <summary>
/// Application-wide, user-configurable settings. Persisted to disk as JSON
/// by <see cref="Interfaces.ISettingsService"/>.
/// </summary>
public sealed class DownloadSettings
{
    public const int MinConnections = 1;
    public const int MaxConnections = 32;
    public const int DefaultConnections = 8;

    /// <summary>
    /// The connection counts offered in the UI. A free 1–32 spinner was a
    /// question with 32 answers and no guidance in it, and the values between
    /// the powers of two buy nothing: what decides a download's speed is
    /// roughly how many sockets the server will serve in parallel, and 13 is not
    /// a meaningfully different answer from 16. These are the steps IDM offers,
    /// and every one of them divides a file into segments a human can reason
    /// about in the details window.
    /// </summary>
    public static readonly IReadOnlyList<int> ConnectionChoices = new[] { 1, 2, 4, 8, 16, 24, 32 };

    /// <summary>
    /// Default loopback port for the browser bridge. Deliberately high and
    /// unassigned by IANA, so it doesn't collide with something the user is
    /// likely to already be running.
    /// </summary>
    public const int DefaultBrowserIntegrationPort = 9614;

    /// <summary>Default number of simultaneous connections for new downloads (1-32).</summary>
    public int DefaultConnectionsCount { get; set; } = DefaultConnections;

    /// <summary>Maximum retry attempts per connection before it is marked Failed.</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>Base delay between retries; grows with exponential backoff.</summary>
    public int RetryDelayMilliseconds { get; set; } = 1500;

    /// <summary>
    /// Folder finished downloads are saved to by default. An ordinary setting
    /// with an ordinary default — Options still offers it, and Add Download's
    /// "Save to" overrides it for a single file. Unlike <see cref="TempFolder"/>
    /// below, where this points is the user's business.
    /// </summary>
    public string DefaultDownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "QuickByte");

    /// <summary>
    /// Folder used to store in-progress chunk (.part) files: <c>temp\</c>
    /// beside settings.json.
    /// </summary>
    /// <remarks>
    /// Not a setting, which is why it is
    /// <see cref="JsonIgnoreAttribute">ignored</see> by the serializer and
    /// re-derived by <see cref="Services.SettingsService"/> from wherever the
    /// data folder is. Options no longer offers it: there is one right answer,
    /// and the wrong ones are expensive. The old default put these chunks under
    /// <c>%TEMP%</c>, which the user, Disk Cleanup and every third-party cleaner
    /// empty on a schedule — and since resume is driven by chunk length on disk,
    /// a sweep during a pause silently costs the user the whole download.
    /// Ignoring the persisted value is also what migrates an existing install
    /// off that path; keeping it would strand exactly the people who already
    /// have the problem, with no field left to fix it in.
    /// </remarks>
    [JsonIgnore]
    public string TempFolder { get; set; } = AppPaths.Temp;

    /// <summary>
    /// How often (ms) aggregated progress events are pushed to the UI. Kept low
    /// on purpose: the windows interpolate between samples at ~60 fps, and a
    /// short sampling interval is what makes that interpolation track reality
    /// instead of lagging visibly behind it.
    /// </summary>
    public int ProgressUpdateIntervalMilliseconds { get; set; } = 100;

    public const int MinBufferSizeBytes = 4 * 1024;
    public const int MaxBufferSizeBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Buffer size (bytes) used for stream copy operations. It has no field in
    /// Options, which is exactly why every consumer runs it through
    /// <see cref="ClampBufferSize"/>: the only way it can hold a number is by
    /// being edited into settings.json, and a 0 there would otherwise reach a
    /// <c>new byte[…]</c> and a FileStream constructor and break every download
    /// with an error that names none of this.
    /// </summary>
    public int StreamBufferSizeBytes { get; set; } = 81920; // 80 KB

    /// <summary>Maximum number of downloads actively running at once (queue throttling).</summary>
    public int MaxConcurrentDownloads { get; set; } = 3;

    /// <summary>
    /// Cap shared by every running download, in bytes per second; <c>0</c> means
    /// unlimited. Unlike the rest of this class it is honoured live — see
    /// <see cref="Services.DownloadManager"/>.
    /// </summary>
    public long GlobalSpeedLimitBytesPerSecond { get; set; }

    /// <summary>
    /// Whether the loopback bridge the browser extension talks to is listening.
    /// Honoured live, like the global speed limit — see
    /// <see cref="Services.BrowserIntegrationServer"/>.
    /// </summary>
    public bool BrowserIntegrationEnabled { get; set; } = true;

    /// <summary>Loopback port for that bridge. Must match the extension's setting.</summary>
    public int BrowserIntegrationPort { get; set; } = DefaultBrowserIntegrationPort;

    /// <summary>
    /// Shared secret the extension presents on every request. Generated on first
    /// use rather than defaulted, since a fixed default would be no secret at
    /// all — every QuickByte install would accept every install's extension.
    /// Not sensitive enough for <see cref="Helpers.SecretProtector"/>: it only
    /// unlocks a prompt on this machine, and it has to be readable to be pasted
    /// into a browser.
    /// </summary>
    public string BrowserIntegrationToken { get; set; } = string.Empty;

    /// <summary>
    /// Register QuickByte to launch when the user signs in. The flag is the
    /// user's intent; the registration itself is a Windows concept and lives in
    /// the UI layer (<c>UI/StartupRegistration.cs</c>) — Core stores the
    /// preference and never touches the registry.
    /// </summary>
    public bool StartWithWindows { get; set; }

    /// <summary>
    /// Start with no window on screen, only the notification-area icon. Applies
    /// to every launch, not just the one Windows performs at sign-in: a user who
    /// asked for a background download manager means it either way.
    /// </summary>
    public bool StartMinimized { get; set; }

    /// <summary>Pop the download details window open as soon as a download is added.</summary>
    public bool AutoOpenDetailsWindow { get; set; } = true;

    /// <summary>Show the "download complete" window when a download finishes.</summary>
    public bool ShowCompletionWindow { get; set; } = true;

    public int ClampConnections(int requested) =>
        Math.Clamp(requested, MinConnections, MaxConnections);

    /// <summary>
    /// The entry of <see cref="ConnectionChoices"/> a number belongs to — the
    /// nearest one at or below it, so nothing is ever quietly rounded *up* into
    /// more sockets than were asked for.
    ///
    /// The drop-downs need it because the number they are loading was not
    /// necessarily typed into a drop-down: a settings.json written by an older
    /// build holds whatever the old 1–32 spinner allowed, and a combo box asked
    /// to select 5 selects nothing at all and reads back as the first item.
    /// </summary>
    public static int NearestConnectionChoice(int requested)
    {
        int best = ConnectionChoices[0];
        foreach (int choice in ConnectionChoices)
        {
            if (choice > requested) break;
            best = choice;
        }
        return best;
    }

    /// <summary>
    /// <see cref="StreamBufferSizeBytes"/> made safe to hand to a buffer
    /// allocation or a <see cref="FileStream"/>. See that property for why.
    /// </summary>
    public int ClampBufferSize() =>
        Math.Clamp(StreamBufferSizeBytes, MinBufferSizeBytes, MaxBufferSizeBytes);
}
