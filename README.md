# QuickByte

A C# / WinForms internet download manager, built in the spirit of IDM
(Internet Download Manager): multi-connection segmented downloads, live
per-connection progress, pause/resume/retry, and a synchronized UI across
the main window and any number of open download-detail windows.

## Solution layout

```
QuickByte.sln
Directory.Build.props      Single source of truth for the version and assembly metadata
src/
  QuickByte.Core/         Class library — all business logic, zero WinForms references
    Enums/                DownloadStatus, ConnectionStatus, DownloadCategory
    Models/                DownloadItem, DownloadSettings, ConnectionInfo, RemoteFileInfo
    Events/                Event-arg DTOs used for progress/status/connections/list-changed
    Interfaces/           Contracts for every service (see Architecture below)
    Services/              Concrete implementations
    Helpers/                ByteFormatter, RangeSplitter, SpeedCalculator, RetryPolicy,
                              FileNameHelper, BandwidthLimiter, ProductVersion

  QuickByte.UI/            WinForms application
    Program.cs             Composition root (wires Core services together)
    AppDispatcher.cs        Marshals Core events onto the UI thread
    SingleInstance.cs       Mutex + named pipe: one copy per user, URLs handed to it
    AppVersion.cs           Reads the build's version back off its own assembly
    Forms/                  MainForm, DownloadDetailsForm, NewDownloadForm, SettingsForm,
                              UpdateForm
    Controls/                ListViewProgressPainter, ConnectionSegmentsBar, IconFactory,
                              BrandIcon, IcoWriter (shared owner-draw progress bar, the
                              connection start-position/progress bar, and every toolbar/
                              tree/file icon — all drawn with GDI+)
    Assets/                 quickbyte.ico — the one real image file, generated from
                              BrandIcon.cs so the shell icon matches the drawn one
```

No third-party NuGet packages are required — everything is built on the
.NET base class library (`System.Net.Http`, `System.Text.Json`,
`System.Windows.Forms`).

## Building and running

Requires the **.NET 8 SDK** and (to actually run the UI) **Windows**, since
`QuickByte.UI` targets `net8.0-windows` with WinForms.

```
dotnet build QuickByte.sln
dotnet run --project src/QuickByte.UI/QuickByte.UI.csproj
```

`QuickByte.Core` alone targets plain `net8.0` and will build/test on any OS.

### Versioned builds

The product version lives in one place — `Directory.Build.props` — and flows
into both assemblies, the .exe's file properties, the window title and the
About box. A build server can add a build number or a pre-release label without
editing the file:

```
dotnet build QuickByte.sln -p:BuildRevision=137     -> FileVersion 1.1.0.137
dotnet build QuickByte.sln -p:VersionSuffix=beta.1  -> ProductVersion 1.1.0-beta.1
```

`AssemblyVersion` is deliberately pinned to `major.minor.0.0` so a patch release
never breaks an assembly compiled against `QuickByte.Core`.

## Architecture

**Separation of concerns.** All downloading logic — HTTP, threading, file
I/O, retry, merging — lives in `QuickByte.Core` and has no reference to
`System.Windows.Forms`. The UI project only builds windows and wires them
to Core's events. This means the download engine could be reused headless
(a CLI, a service) without touching a line of it.

**The download pipeline, top to bottom:**

1. **`IRemoteFileInfoProvider`** (`RemoteFileInfoProvider`) — resolves file
   name, size, content type, and Range-request support via a HEAD request,
   falling back to a ranged GET probe (`bytes=0-0`) for servers that reject
   HEAD.
2. **`IDownloadManager`** (`DownloadManager`) — the facade/registry every
   window talks to. Owns every `IDownloadService`, persists the download
   list to JSON, throttles concurrent downloads with a `SemaphoreSlim`, and
   re-publishes each service's events under one set of manager-level
   events — the single shared source of truth that keeps the main window
   and every open details window in sync.
3. **`IDownloadService`** (`DownloadService`) — orchestrates one download's
   lifecycle (`Queued → Connecting → Downloading → Merging → Completed`,
   or `Paused` / `Failed` / `Cancelled`). Delegates byte-fetching to the
   pool manager and final assembly to the file merger.
4. **`IConnectionPoolManager`** (`ConnectionPoolManager`) — splits the
   total size into up to 32 byte ranges (`RangeSplitter`), creates one
   `IDownloadConnection` per range via the `IConnectionFactory` (Factory
   pattern), runs them concurrently, and raises **throttled, aggregated**
   progress events off a `Timer` (default every 300 ms) rather than
   forwarding every connection's raw byte events — this is what keeps the
   UI smooth regardless of how many connections are active.
5. **`IDownloadConnection`** (`DownloadConnection`) — downloads exactly one
   byte range into its own temp `.tmp` chunk file via an HTTP `Range`
   request. Fully independent of its siblings; retries transient failures
   internally using `RetryPolicy` (Strategy pattern, exponential backoff).
6. **`IFileMerger`** (`FileMerger`) — once every connection finishes,
   concatenates the ordered chunk files into the final destination file
   and cleans up the temp folder.

**Design patterns used:**
- **Factory** — `IConnectionFactory` / `HttpConnectionFactory` decouple the
  pool manager from the concrete HTTP connection implementation.
- **Strategy** — `RetryPolicy` is a pluggable retry strategy shared by every
  connection.
- **Facade / Registry** — `DownloadManager` is the single entry point the UI
  uses instead of talking to individual services directly.
- **Observer** — every layer communicates upward via C# events
  (`ProgressChanged`, `StatusChanged`, `ConnectionsChanged`, etc.), and
  `IDispatcher` abstracts "marshal this to the UI thread" so Core stays
  UI-framework-agnostic while the WinForms layer supplies the real
  `SynchronizationContext`-based implementation.
- **Repository** — `IDownloadRepository` / `DownloadRepository` persist the
  download list as JSON so state survives an app restart.

**Multi-window synchronization.** `MainForm` and any number of open
`DownloadDetailsForm` windows never talk to each other directly. Both
subscribe only to events from `IDownloadManager` (or, for connection-level
detail, directly to the relevant `IDownloadService`) — the same event
stream, marshaled through the same `IDispatcher`. That shared source of
truth is what guarantees every open window shows identical numbers at the
same time, with no polling and no risk of windows drifting out of sync.

**Performance notes:**
- A single, shared, pooled `HttpClient`/`SocketsHttpHandler` is used for
  all connections (avoids per-request socket exhaustion).
- Connections update thread-safe counters (`Interlocked`) without raising
  per-byte events; the pool manager samples them on a timer and raises one
  aggregated event, bounding UI update frequency regardless of connection
  count or transfer speed.
- `ListView` rows are updated in place (`Dictionary<Guid, ListViewItem>`)
  rather than rebinding the whole list on every tick, and progress bars are
  owner-drawn directly into the existing cell — no extra child controls per
  row.
- Speed/ETA are smoothed over a rolling time window (`SpeedCalculator`)
  rather than computed from instantaneous deltas, avoiding jittery numbers.
- Bandwidth limiting uses a token bucket shared by all of a download's
  connections, so the cap applies to the transfer rather than to each segment.
  A connection asks for an allowance *before* it reads and takes no more than
  it was granted, which is what keeps eight parallel connections from
  overshooting the limit eightfold; unused allowance is refunded. With no limit
  set the request path short-circuits before taking a lock, so the feature
  costs nothing when it is off.

**Single instance.** A named mutex keeps QuickByte to one copy per user — two
processes sharing one `downloads.json` would clobber each other's state.
Launching it again (with a URL, say from a browser) hands the link to the
running window over a named pipe and exits, rather than starting a rival
download engine.

## Update checker

QuickByte checks for a newer release at every launch, and on demand from
**Help > Check for Updates**. Both paths read one hardcoded HTTPS endpoint
(`UpdateService.DefaultManifestUrl`) and expect a small JSON manifest:

```json
{
  "version": "1.4.0",
  "downloadUrl": "https://example.com/releases/QuickByte-1.4.0-Setup.exe",
  "releaseNotes": "What changed in this release.",
  "releaseDate": "2026-08-21T09:00:00Z",
  "fileSizeBytes": 7340032,
  "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08"
}
```

Only `version` and `downloadUrl` are required. Version comparison is done by
`ProductVersion`, which tolerates a leading `v`, a missing third component and
a `-prerelease` suffix — and treats `1.4.0` as newer than `1.4.0-beta.1`, so a
beta build is not offered an "update" to itself.

The two paths differ in exactly one respect, and deliberately:

- **At startup** the check is silent. If it finds something it *offers* it in
  the update window and downloads nothing until the user clicks Update Now; if
  the endpoint is unreachable it says nothing at all, because nobody asked.
- **From the Help menu** the user has already asked, so the same window opens
  already downloading and runs the installer as soon as it lands. This path
  also reports back when there is nothing new, or when the check failed.

Once the installer starts, QuickByte closes itself — setup cannot replace files
the running process holds open. The installer is fetched to
`%TEMP%/QuickByte/updates`, must be served over HTTPS, and is verified against
`sha256` when the manifest supplies one; a mismatched or partial download is
deleted rather than left on disk.

## Configurable settings

Exposed via **Settings** in the main window toolbar, all settings map
directly to `DownloadSettings`:

| Setting | Range / default |
|---|---|
| Default connections per download | 1–32, default 8 |
| Max retries per connection | default 3 |
| Retry base delay | default 1500 ms (exponential backoff) |
| Max concurrent downloads | default 3 |
| Global speed limit | default 0 (unlimited), in KB/s |
| Progress sampling interval | default 100 ms (clamped to 50–2000) |
| Default download folder | `~/Downloads/QuickByte` |
| Temp folder (chunk files) | OS temp + `QuickByte` |
| Open the details window automatically | on |
| Show the download complete window | on |

## UI

Every window is styled after classic IDM, on a single flat palette defined in
`UI/Controls/Theme.cs`:

- **Main window** — menu bar (File/Tasks/Downloads/View/Help), an icon
  toolbar (Add URL, Resume, Pause, Stop All, Delete, Delete Completed,
  Properties, Options), a category tree sidebar (All Downloads, grouped by
  file type, plus Unfinished/Finished/Failed/Queues), a downloads grid with a
  per-file-type icon and an owner-drawn progress bar, a right-click context
  menu on each row, and a status bar showing download counts and aggregate
  speed.
- **Download details window** — opens automatically when a download starts.
  Three tabs (Download status, **Speed Limiter**, Options on completion). The
  Speed Limiter tab caps this download in KB/s and applies the moment it is
  changed, including to a transfer already in flight. The
  Download status tab shows the file/URL header, the
  status/size/downloaded/speed/ETA/connections/resumable fields, an overall
  progress bar, a collapsible "Hide details" section, the segmented "start
  positions and download progress by connections" bar, and a connections grid
  (#, Downloaded, Progress, Info).
- **Download complete window** — replaces the details window when a transfer
  finishes: final size, average speed, destination folder, and Open File /
  Open Folder actions. Opting out of it from the checkbox is persisted.
- **Add New Download** pre-fills from the clipboard and resolves file
  info (type, size, resumability) before you commit; **Options** groups
  settings into Connection / Folders / Interface tabs.
- **Update window** — installed version against the offered one, the release
  notes, and a progress bar for the installer download. The same window serves
  the startup prompt and a manual check; see Update checker above.

Progress never steps or rewinds: the engine samples every ~100 ms and each
window interpolates between samples at ~60 fps (`ProgressAnimator<TKey>`,
`SmoothProgressBar`, `ConnectionSegmentsBar`). While chunks are being merged
the bar stays full and reports merge progress as overlay text, because the
bytes are already on disk.

All icons are generated at runtime with GDI+ (`IconFactory`), including the
QuickByte logo itself (`BrandIcon`). The single image file in the repo,
`Assets/quickbyte.ico`, is generated from that same drawing code by `IcoWriter`
— it exists only because the compiler needs a real file to stamp into the
executable's Win32 resources, and generating it means the icon Explorer shows
can never drift from the one the windows draw.

## Known simplifications

This is a complete, working reference implementation, not a byte-for-byte
IDM clone. A few things a production build would add:
- A signed installer.
- Scheduling (start/stop a queue at a given time).
- Browser integration (link capture).
- Automated tests (the interface-driven design makes the Core layer easy
  to unit test with fakes — `IConnectionFactory` and `IDownloadConnection`
  in particular are there specifically to make the pool manager testable
  without real network calls).
