# QuickByte

A C# / WinForms internet download manager, built in the spirit of IDM
(Internet Download Manager): multi-connection segmented downloads over HTTP,
HTTPS and FTP, live per-connection progress, pause/resume/retry, and a
synchronized UI across the main window and any number of open download-detail
windows. A Chrome extension takes downloads over from the browser.

## Solution layout

```
QuickByte.sln
Directory.Build.props      Single source of truth for the version and assembly metadata
src/
  QuickByte.Core/         Class library — all business logic, zero WinForms references
    Enums/                DownloadStatus, ConnectionStatus, DownloadCategory,
                            QueueState, ScheduleDays
    Models/                DownloadItem, DownloadSettings, ConnectionInfo, RemoteFileInfo,
                            DownloadQueue, QueueSchedule
    Events/                Event-arg DTOs used for progress/status/connections/list-changed
    Interfaces/           Contracts for every service (see Architecture below)
    Services/              Concrete implementations
      Ftp/                    Minimal FTP/FTPS client: control channel, info provider,
                                segment connection, factory
    Helpers/                ByteFormatter, RangeSplitter, SpeedCalculator, RetryPolicy,
                              FileNameHelper, BandwidthLimiter, ProductVersion,
                              SecretProtector, UrlCredentials
    Streaming/              HLS playlist and DASH manifest parsers, StreamPlan (which
                              pieces to fetch, and which streams to refuse),
                              StreamResolver (the qualities a playlist offers)
    Exceptions/             AuthenticationRequiredException, StreamNotSupportedException

  QuickByte.UI/            WinForms application
    Program.cs             Composition root (wires Core services together)
    AppDispatcher.cs        Marshals Core events onto the UI thread
    SingleInstance.cs       Mutex + named pipe: one copy per user, URLs and queue
                              requests handed to it
    StartupRegistration.cs  "Start with Windows" — the HKCU Run entry for the app
    QueueAgentRegistration.cs  The same, for the scheduler agent below
    AppVersion.cs           Reads the build's version back off its own assembly
    Forms/                  MainForm, DownloadDetailsForm, NewDownloadForm, SettingsForm,
                              QueueManagerForm, UpdateForm
    Controls/                ListViewProgressPainter, ConnectionSegmentsBar, IconFactory,
                              BrandIcon, IcoWriter (shared owner-draw progress bar, the
                              connection start-position/progress bar, and every toolbar/
                              tree/file icon — all drawn with GDI+)
    Assets/                 quickbyte.ico — the one real image file, generated from
                              BrandIcon.cs so the shell icon matches the drawn one

  QuickByte.Cli/           qb.exe — the command-line tool. Parses arguments, talks to the
                             running app over a per-user named pipe, prints the answer.
                             See "Command line" below.

  QuickByte.Agent/         Windowless scheduler that outlives the app: reads queues.json,
    Program.cs               and launches QuickByte when a queue's schedule comes due.
    SchedulerLoop.cs         Starts with the user's session; exits by itself when no
    AgentLog.cs              queue is scheduled. See "Queues and scheduling" below.

browser/
  chrome-extension/        Manifest V3 extension: captures Chrome's downloads and
                             hands them to QuickByte over the loopback bridge
```

One NuGet package: `System.Security.Cryptography.ProtectedData`, so FTP and HTTP
passwords are not written to disk in plain text. For `QuickByte.exe` it is
compile-time only and deploys nothing — `ProtectedData` is missing from the
`net8.0-windows` targeting pack `QuickByte.Core` builds against, but ships inside
the `Microsoft.WindowsDesktop.App` framework the app runs on, so the SDK strips
the runtime asset.

The agent is the one exception, and it is worth knowing before wondering why the
DLL is in the install folder: being windowless it runs on plain
`Microsoft.NETCore.App`, which does *not* carry `ProtectedData`, so the SDK keeps
the file. `System.Security.Cryptography.ProtectedData.dll` therefore ships beside
`QuickByte.Agent.exe` and is named individually in the installer's `[Files]`
list. Everything else is the base class library (`System.Net.Http`,
`System.Net.Sockets`, `System.Text.Json`, `System.Windows.Forms`).

## Building and running

Requires the **.NET 8 SDK** and (to actually run the UI) **Windows**, since
`QuickByte.UI` targets `net8.0-windows` with WinForms.

```
dotnet build QuickByte.sln
dotnet run --project src/QuickByte.UI/QuickByte.UI.csproj
```

The solution builds three projects: the engine, the WinForms app, and
`QuickByte.Agent` — the scheduler that starts queues while the app is closed.

The agent has to end up **beside `QuickByte.exe`**: the app looks for it in its
own folder, and the agent finds the app the same way. Building `QuickByte.UI`
puts it there — its `CopySchedulerAgent` target copies the agent's output into
the app's, and fails the build rather than copying nothing, because a scheduler
that is silently absent is indistinguishable from one that is broken. The
installer's `[Files]` list then packages it from that same folder. Nothing needs
doing by hand in either place.

A copy that somehow ships without it still schedules queues for as long as its
window (or tray icon) is there, and the queue window says exactly that rather
than promising something it cannot do.

`QuickByte.Core` alone targets plain `net8.0` and will build/test on any OS.

### Versioned builds

The product version lives in one place — `Directory.Build.props` — and flows
into every assembly, the .exe's file properties, the window title and the
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

1. **`IRemoteFileInfoProvider`** (`ProtocolFileInfoProvider`) — resolves file
   name, size, content type, and segment support, dispatching on the URL's
   scheme. `RemoteFileInfoProvider` handles HTTP(S) with a HEAD request
   followed by a ranged GET probe (`bytes=0-0`); `FtpFileInfoProvider` logs in
   and asks `SIZE`, `MDTM` and `FEAT`. A `401` or an FTP `530` is reported as
   `AuthenticationRequiredException` rather than a generic failure, which is
   what brings up the sign-in dialog instead of an error the user can only
   read. For HTTP only the ranged GET's `401` counts: HEAD can be routed
   somewhere GET is not, and github.com sends a signed-in browser's HEAD to a
   host that refuses everyone. The probe is what settles segment support, and
   it is never skipped — see **Segment support is proven, not read off a
   header** below.
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

   **Dynamic segmentation.** A fixed split is only as fast as its slowest
   range: when seven connections finish and the eighth crawls, the download
   crawls on one connection. So when a connection finishes, its slot goes to the
   back half of whichever running range has the most left — that range is
   shortened while it streams and a new connection fetches the rest into a chunk
   of its own. Two rules keep it correct: a connection *reserves* each read before
   making it, and a range can only be shortened beyond everything written or
   reserved, so no byte ever lands in two chunks; and the layout is written to
   `segments.json` before the new connection writes anything, so resume after a
   crash knows exactly which chunk holds which bytes. It never runs more
   connections than the download's count, skips ranges under 1 MiB, and — since a
   rate-limited server can answer the new connection with `429` — the first
   failure stops splitting and a split-off range is retried quietly rather than
   failing the download. Options → Connection can turn it off.
5. **`IDownloadConnection`** (`DownloadConnection` / `FtpDownloadConnection`)
   — downloads exactly one byte range into its own temp `.tmp` chunk file:
   over HTTP with a `Range` request, over FTP with `REST` plus a byte count
   (FTP can only be told where to *start*, so the segment's end is enforced
   by closing the data connection). Fully independent of its siblings;
   retries transient failures internally using `RetryPolicy` (Strategy
   pattern, exponential backoff), which deliberately does not retry a
   rejected password.
6. **`IFileMerger`** (`FileMerger`) — once every connection finishes,
   assembles the ordered chunk files into the final destination file,
   deleting each chunk as soon as its bytes are safely in the output. See
   **Disk space and the merge** below.

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
  download list as JSON so state survives an app restart; `IQueueRepository` /
  `QueueRepository` do the same for the queue list, with one extra rule (see
  Queues and scheduling: a second process reads that file).

**Queues sit beside the engine, not inside it.** `IQueueManager` /
`QueueManager` own the queues and drive downloads only through
`IDownloadManager` — the download pipeline has no idea queues exist. A queue is
a policy about *when* to call Resume and *how fast* to let the result go, and
keeping it out of the pipeline is what stops six-layer plumbing from growing a
seventh concern. See Queues and scheduling below.

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

## Queues and scheduling

A **queue** is a named, ordered list of downloads with its own settings: how
many of them run at once, a speed limit they share, and — the point of the
feature — when the queue starts itself. This is IDM's model, and it is the
reason a download manager can be told "fetch these eleven files overnight,
three at a time, at half my line rate" and then be closed.

Queues live in `%AppData%/QuickByte/queues.json`, next to `downloads.json`.
Membership is an ordered list of download ids **on the queue**, not a `QueueId`
field on each download: order is half of what a queue is, and one owner means
the two files can never disagree about what is in one.

**Running a queue.** `QueueManager` gives a running queue one runner task. The
runner is a loop rather than a `Task.WhenAll` over a snapshot, so it re-reads
the queue on every pass: a download appended while it runs is picked up, a
concurrency change takes effect on the next free slot, and a stop time can end
the run between downloads. Each download is started **at most once per run** —
without that rule a queue would instantly restart a download the user had just
paused, and a download that failed to start at all would spin the loop. Queued
downloads are started through `IDownloadManager.ResumeAsync` like any other, so
the app-wide concurrent-download limit and the global speed limit still apply on
top of the queue's own; a queue asking for eight on an app configured for three
gets three.

**The three speed tiers.** A running download's connections share a composite
limiter of *its own* limit, *its queue's* limit, and the *global* one. They are
separate limiters rather than one number because they mean different things: the
per-download cap is the user's choice and is persisted on the item, the queue
cap belongs to whichever queue happens to be running the file and is lifted the
moment it finishes or leaves. All three apply mid-transfer. A download in no
queue pays nothing for the tier — an unset limiter short-circuits before it
takes a lock.

**Scheduling is a window, not an instant.** A schedule that only knew its start
time could only fire if something happened to be watching the clock at exactly
that minute. `QueueSchedule` instead answers "is this queue supposed to be
running right now?": days of the week, a start time, and either an explicit stop
time (which may cross midnight — 22:00 until 06:00 needs no second date field)
or a one-hour grace period. That is what lets a run that was missed because the
machine was asleep still happen when the machine comes back, and what stops the
first launch of the week from starting every schedule it slept through.

**Two watchers, one file, one verdict.** While QuickByte is open, a 20-second
timer in `QueueManager` starts due queues. While it is *not* — which is most of
the time, and certainly at 03:00 — that job belongs to **`QuickByte.Agent`**, a
separate, windowless process:

- It starts with the user's session from its own `HKCU\...\Run` entry, written
  and re-asserted by the app (`QueueAgentRegistration`) whenever any queue has a
  schedule, and removed again when none does.
- It reads the same `queues.json` and asks Core the same question — literally
  the same method, `DownloadQueue.IsDue` — so the two can never disagree by a
  minute and start a queue twice.
- When a queue is due it launches `QuickByte.exe --run-queue {id} --minimized`.
  If QuickByte is already running, that launch is handed over the existing
  single-instance pipe and the running copy starts the queue; the agent skips
  even that when it can see the app's mutex, and lets the in-app timer do it.
- It downloads nothing, holds no state of its own, and **exits by itself** once
  no queue has a schedule left — a user who never schedules anything never has a
  background process.
- `DownloadQueue.LastRunAt` is written the moment a run starts and persisted, so
  neither watcher starts the same window twice.
- It writes one line per decision to `%AppData%/QuickByte/agent.log`, and
  `QuickByte.Agent.exe --once` performs a single evaluation and exits. A
  windowless process has no other way to answer "why didn't my queue run?".

**Nothing is registered until you schedule something — and the agent never
registers itself.** This surprises people, so it is worth stating plainly: a
fresh install puts *no* scheduler in Task Manager's Startup tab. The `Run` entry
is written by the **app**, in `QueueAgentRegistration.Sync`, which runs at every
launch and again on every queue change (`MainForm.OnQueuesChanged`). The
condition is `HasScheduledQueues` — any queue whose schedule is actually
enabled — so:

- Tick **Enable schedule** on a queue and the entry appears immediately. The
  same call also `Process.Start`s the agent then and there, so the first
  schedule is watched that afternoon rather than from the next sign-in.
- Untick the last one and the entry is removed again. The running agent is not
  killed; it reads the same file, finds no schedules left, and exits by itself
  within ~30 seconds.
- The agent project contains **no registry code at all** — no
  `Microsoft.Win32`, no `Registry`, in it or in Core. Running
  `QuickByte.Agent.exe` by hand, with or without `--once`, evaluates schedules
  and exits; it will never put anything in Startup. That is deliberate: a
  process that registered its own autostart could not be switched off by
  removing the registration.

**Two entries, two different things.** Under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`:

| Value name | Written by | What it is | Task Manager shows |
|---|---|---|---|
| `QuickByte` | `StartupRegistration` | Options > System's *Start with Windows* | QuickByte |
| `QuickByteScheduler` | `QueueAgentRegistration` | The queue scheduler agent | QuickByte Queue Scheduler |

They are independent: either can be present without the other, and the labels in
Task Manager come from each executable's `FileDescription`, not from the value
name — so the agent is listed as **QuickByte Queue Scheduler**, not as
`QuickByte.Agent`. Task Manager also does not always notice a value written
while its window is open; reopen it before concluding the entry is missing.

Both writes are best-effort and silent. Group policy or a security product can
refuse them, and a queue window is not the place to learn about it — the
schedule still runs for as long as QuickByte itself is open. Uninstalling clears
`QuickByteScheduler` as well: setup carries a delete-on-uninstall entry for it,
so removing QuickByte cannot leave a `Run` value pointing at an agent that is no
longer there.

**Why not a Windows service or a boot-time task?** Because both need an
administrator to install, and neither is the right shape. A service runs before
anyone signs in, as SYSTEM, with no access to the user's download folder,
credentials or cookies — everything a download manager needs. Registering at
sign-in per user needs no elevation, is visible (and switchable off) in Task
Manager's Startup tab, and is the earliest moment a per-user download queue
means anything at all. The agent survives QuickByte closing, crashing or being
updated, which is the property that was actually being asked for.

**Waking a sleeping computer.** A queue's Schedule tab has **Wake the computer
to start it**. The agent cannot do that on its own — nothing runs while the
machine sleeps — so QuickByte registers a Task Scheduler task for the user,
`QuickByte Queue Wake (<user>)`, with one weekly trigger per start time
(`WakeTimes`: queues starting at the same minute share one, on all their days)
and `WakeToRun` set. That is the one place a program without administrator
rights can set a persistent wake timer; it was checked on a standard account
rather than assumed.

- The task runs as the signed-in user, only while they are signed in, with
  least privilege, and its action is `QuickByte.Agent.exe --once` — one look at
  the schedules, which finds the queue due and launches QuickByte.
- It is written through `schtasks.exe` (by full path) from an XML definition,
  re-asserted at each launch and on every queue change, and deleted when no
  queue asks for a wake. Uninstalling deletes it too. `QueueWakeRegistration`
  skips the process entirely when nothing changed.
- **Windows must allow wake timers** (Power Options > Sleep > Allow wake
  timers). On many laptops they are off on battery, and Modern Standby machines
  honour them only on AC power; the queue window says so when wake is ticked.
- Once awake, the machine has to *stay* awake, or Windows puts it back to sleep
  two minutes after a wake nobody touched. **Keep the computer awake while
  downloads are running** (Options > System, on by default) holds a power
  request while anything is downloading (`KeepAwake`) — idle sleep only; the
  lid, the power button and Start > Sleep still work, and `powercfg /requests`
  names QuickByte while it holds one.

Without **Wake the computer**, a queue scheduled while the machine is asleep
starts when it wakes, if that is within the schedule's window.

**When a queue finishes.** A queue's Options tab also says what to do once a
run has worked through every file: nothing, exit QuickByte, sleep, hibernate or
shut down — the other half of "fetch these overnight". It is a property of the
queue, so a schedule set for every night keeps doing it, and only a run that
*drains* the queue triggers it: one the user stopped, or one its own stop time
cut short with files left, does not. A single download can ask for the same
from its details window's **Options on completion** tab, beside opening its
folder and playing a sound; those choices are persisted on the item, so they
apply whether or not the window is still open.

Nothing turns a computer off without a countdown first — a minute for sleep,
hibernate and shut down, fifteen seconds to exit — with **Cancel** as the
default button, and a note of how many other downloads are still running and
will be paused. Sleep and hibernate go through WinForms'
`Application.SetSuspendState`; shutting down runs Windows' own `shutdown.exe`
from the system directory, by full path, without `/f`, so an app with unsaved
work still gets to object.

**In the UI.** The sidebar's **Queues** branch lists every queue with its file
count and filters the list to it. **Tasks > Queues & Scheduler** (and the
toolbar's Queues button) opens the queue window: queues on the left, and for the
selected one its files in queue order (with move up/down/take out), its
Options — name, downloads at once, queue speed limit — and its Schedule. Edits
apply as they are made rather than behind an OK button, because the queue may be
running while the window is open and another process is reading its schedule.
Downloads join a queue from the main window's right-click menu (**Add to
Queue**), or from the Add New Download dialog's **Queue** field — choosing a
queue there adds the download without starting it, which is the whole point.

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

A completed update leaves its installer there as well — setup cannot delete the
file it is running from, and QuickByte is gone by then — so the next launch
sweeps the folder, retrying briefly in case setup is still finishing, and removes
the folder once it is empty.

## Protocols and authentication

`ProtocolFileInfoProvider` and `ProtocolConnectionFactory` pick an
implementation from the URL's scheme, so nothing further down the pipeline
knows which protocol answered — the pool splits a range and runs N workers
either way.

**FTP** (`Core/Services/Ftp/`) is a small client written directly on
`TcpClient`: `FtpControlChannel` connects, logs in (anonymously when no
credentials are given), switches to binary, and opens a passive data
connection with `PASV`, or `EPSV` over IPv6. `ftps://` negotiates explicit TLS
(`AUTH TLS`, then `PBSZ 0` / `PROT P` so the data channel is encrypted too).
The BCL's `FtpWebRequest` was not used: it has been obsolete since .NET 6 with
no non-obsolete way to construct one, and it hides whether the server actually
honoured `REST` — without which resume writes bytes at the wrong offset and
corrupts the file silently. Segmented FTP therefore depends on `FEAT`
advertising `REST STREAM`; when it doesn't, the download drops to a single
connection, and a partial chunk that cannot be continued is discarded and
refetched rather than appended to.

**Passive-mode note:** the IP address in a `227` reply is ignored in favour of
the host the control connection already reached. Servers behind NAT routinely
announce a private address there, and dialling it is the classic "passive mode
hangs forever" failure.

### Segment support is proven, not read off a header

Whether a file is split across connections is decided by one question, and
getting it wrong optimistically does not produce a slow download — it produces
a failed one. Seven of eight connections are handed a response that starts at
byte zero, refuse to write it at their own offset, and exhaust their retries.

`Accept-Ranges: bytes` is not enough to answer that question, because the thing
sending it is frequently not the origin. A CDN edge holding the whole file will
answer a `Range` request with the entire body and a `200` while passing the
origin's `Accept-Ranges` straight through. Nothing about that response is an
error in HTTP. So:

- **Only a `206` from the offset that was asked for** sets
  `SupportsRangeRequests`. HEAD may name and size the file; it never gets to
  settle this, however completely it answered.
- **A `200` to the probe from a server that claimed `Accept-Ranges`** is a
  contradiction rather than an answer, and it is the signature of a cache
  rather than of a server that cannot seek. The probe is asked once more with
  `Cache-Control: no-cache` (and its HTTP/1.0 twin `Pragma`), which is what
  makes a shared cache go and ask the origin.
- **If that comes back `206`**, the file is segmentable after all, and
  `DownloadItem.BypassCache` records that it is only segmentable when asked
  that way — so every connection, and every later resume, asks the same way. A
  connection that went back to the cache would get the same whole-file answer
  the probe just worked around.
- **If it still comes back `200`**, the file runs on one connection. A server
  that never claimed ranges is not asked twice; there is no contradiction to
  investigate.

protonvpn.com's download endpoint is the live example: it advertises
`Accept-Ranges: bytes`, ignores `Range` on a cache hit, and honours it
perfectly once the request reaches the origin. Which edge a given connection
lands on varies, so the failure is intermittent — which is exactly the kind
that gets reported as "it works in other download managers".

### Disk space and the merge

A download that runs out of disk used to find out at the end. The chunks filled
the temp volume for hours, and then the merge copied every one of them into a
new file before deleting any, so for the length of the merge a download needed
twice its size. A 40 GB file on a drive with 50 GB free reached 100% and failed
in the merge — the most expensive moment there is to run out.

Two things changed:

- **The space is checked before the first byte** (`DiskSpace.StillNeeded` /
  `FindShortfall`): what is left to fetch into the temp folder, plus what the
  merge will need at the destination, added together when both are on one
  drive. A download that will not fit fails straight away with
  *Not enough free space on C: — 38.2 GB is needed and 12.5 GB is free*, and
  the merge checks again before it starts. A drive that cannot be measured (a
  network share) does not stop anything. A disk that fills up anyway is no
  longer retried by the connections — space does not come back in a backoff.
- **The merge deletes as it goes.** On the same drive the chunks are appended
  onto the first one, which is then renamed into place, so beyond the chunks
  themselves the merge needs room for only the chunk being appended — and a
  single-connection download's merge is a rename rather than a copy of the
  whole file. On another drive the chunks are copied into the destination,
  still deleting each as it lands.

A merge that deletes its inputs cannot simply start again, and the app exiting
half way through a big merge is ordinary. So before it changes anything the
merger writes `merge.json` into the download's temp folder — the chunks, in
order, with their lengths — and flushes each chunk to disk before deleting it.
On the next start `DownloadService` sees the plan and finishes the merge
directly: no probe (the link may have expired, and every byte is already
local) and no connection pool (the chunk set is half folded together). The
output's length says exactly how far it got; a half-appended chunk is cut off
and appended again. A plan whose chunks are missing is dropped, and the next
run downloads what is gone.

### A resumed file is the same file

Resume trusts the chunks on disk to be the head of the file the server is about
to send the rest of. Nothing in HTTP guarantees that across a pause: a nightly
build republished under the same URL, or an installer replaced by the next
patch, is not an error anywhere. The old chunks and the new tail merge into a
file that is neither, and the download reports success.

So a download remembers which version its chunks came from —
`DownloadItem.RemoteVersion`, the server's `ETag` and `Last-Modified` plus the
host that issued them, persisted — and holds the server to it twice:

- **Before it resumes.** Every probe is compared with the stored version
  (`FileVersionCheck`). A different version throws the chunks away and the file
  starts from zero.
- **While it transfers.** Every ranged request carries `If-Range`. A server
  holding a different version answers with the whole new file and a `200`
  instead of a `206`; the connection refuses to write it
  (`RemoteFileChangedException`), and the pool stops its siblings.

Getting "changed" wrong costs hours of download, so the verdict needs evidence
that does not argue with itself:

- **A different size is decisive**, from any host. It also catches a quieter
  corruption: a length change moves every segment boundary, and the old chunks
  would resume at offsets that no longer belong to them.
- **Validators are compared only from the host that issued them.** A mirror
  redirector sends each request to whichever mirror it picks, and two mirrors
  compute different ETags for the identical file. A connection redirected to
  another host that answers `If-Range` with a `200` is simply asked again
  without it.
- **ETags that disagree while the dates agree decide nothing** — an rsync'd
  mirror keeps the timestamp but may build its ETag from an inode number.
- **A validator the probe's own two requests disagree about is dropped**; it
  was minted per request and describes nothing.
- **A connection's report is confirmed by a fresh probe** before anything is
  deleted. If the probe still describes the same file, it is `If-Range` that
  cannot be trusted on that server: `DownloadItem.IfRangeUnsupported` is set and
  persisted, and nothing is thrown away. A file that changes more than three
  times in one run fails with a message saying so.

Weak ETags are never sent in `If-Range` (RFC 9110 forbids it), and a captured
browser header that is any kind of conditional (`If-None-Match`, `If-Range`, …)
is dropped, because a stale one would turn the probe into a `304`.

### Finished files are marked as coming from the internet

Every browser tags a download with the `Zone.Identifier` alternate data stream —
the Mark of the Web — and a good deal of Windows security reads it: SmartScreen
checks an executable's reputation before it runs, Office opens a document in
Protected View, PowerShell refuses an unsigned script. The browser extension
hands downloads over from Chrome, so before QuickByte wrote the mark, a click in
Chrome produced a file with *fewer* protections than letting Chrome save it.

`MarkOfTheWeb` writes the same stream Chromium does once the merge finishes:
`ZoneId=3`, the referring page and the file's address. Both addresses are
rebuilt from a parsed `Uri` with any user name and password removed, only
http(s)/ftp(s) survive, and nothing that could start a new line gets in — the
referrer is a header the extension was sent, and the stream is parsed line by
line. A volume without alternate streams (FAT32, exFAT, many shares) simply goes
without; it never fails the download.

The Attachment Manager policy *Do not preserve zone information in file
attachments* (`SaveZoneInformation = 1`, machine policy first) turns it off, as
it does for the browsers. `ZoneInformationPolicy` in the UI project reads it,
because the registry is the UI's business, and hands `DownloadManager` the
answer. The self-updater's installer is not marked: it is verified against the
manifest's SHA-256 instead, and a mark would put a SmartScreen prompt in front
of every update.

### Refreshing a download's address

The link is often the only thing wrong with a failed download. Signed URLs —
S3, Google Drive, the download button on most sites behind a login or a CDN —
expire, so a download paused overnight resumes into a `403` while gigabytes of
it sit on disk. **Downloads > Refresh Download Address** (also on the row's
right-click menu) gives it a new link without starting over.

There are two ways to supply one, because an expired link usually cannot just be
pasted — the page that issued it has to issue a fresh one:

- **Paste it** into the window, which probes the new address.
- **Click the download link again in the browser** while the window is open. The
  capture is routed to the waiting window instead of opening Add Download
  (`AddressRefresh.MatchesCapture`: the sizes must match, or the file names when
  either size is unknown), and it brings the page's fresh cookie with it. If the
  download was captured from a page, the window offers to open that page.

Nothing changes until the new address has been judged against the download
(`AddressRefresh.Judge`, built on the version rules above):

| The new address serves… | Result |
|---|---|
| the same size, and no contradicting validator from the same host | **Update** — the download carries on from its chunks. Another mirror of the file counts. |
| a different size | Refused. It is a different file; add it as a new download. |
| the same size, but a different version from the same host | **Start Over** — continuing would merge two versions. |
| a file of unknown size, with progress on disk | The user decides: keep the bytes, or start over. |

A new address that asks for a login raises the sign-in prompt, which makes this
also the way to give a download a password that changed since it was added. The
change goes through `IDownloadManager.ChangeAddress`, which refuses a download
that is running, clears the stale error, and is announced as a status event so
the details window shows the new address.

## Streaming video (HLS and DASH)

A video player never downloads "the video". It reads a playlist — HLS's `.m3u8`
or DASH's `.mpd` — picks a quality, and fetches that quality's pieces one after
another, often with the sound in a playlist of its own. Handing that playlist to
a download manager used to save a text file of addresses.

**Adding one.** When the address Add Download fetches is a playlist
(`StreamDetector`: by content type, or by extension when the server says
`text/plain`), `StreamResolver` reads it and the window grows a **Quality** row:
every quality it offers, best first, labelled `1080p · 5.2 Mbps` (and by codec
when the same picture comes with different sound), plus the sound on its own.
The file name follows the choice's container — `.ts` for MPEG-TS, `.mp4` for
fragmented MP4, `.m4a`/`.aac` for sound — and a playlist named `master.m3u8` gives
way to the folder it sits in, or to the page's title when the browser sent it.

**Downloading one.** A download whose `DownloadItem.Stream` is set is run by
`StreamDownloadService` instead of `DownloadService`, behind the same interface,
statuses and events:

- **The playlist is read again on every run.** Piece addresses are usually
  signed and expire; the playlist hands out current ones.
- **Pieces are fetched in parallel**, up to the download's connection count, each
  retried like a connection and throttled by the same speed limits. A piece that
  fails for good stops new pieces from starting; the ones under way finish, and
  a Retry fetches only what is missing.
- **Resume is by piece.** A finished piece is renamed from `.part` into place; a
  `.part` carries on with a `Range` request. `stream.json` records the playlist's
  shape — a playlist whose piece count changed is a different cut of the video,
  and its old pieces are thrown away — and whether every piece is down, so an
  interrupted merge finishes without a playlist that may have expired.
- **AES-128 is decrypted as each piece is written**, with the key fetched once per
  address (with the page's cookies) and held only in memory. An encrypted piece
  never resumes part way: CBC cannot continue from plaintext.
- **The size is estimated** from the pieces already down until the last one lands.
- **Joining uses the same resumable merger** as ordinary downloads, one output per
  track, then the Mark of the Web and the checksum, as for any file.

**What is refused**, in the Add window before anything is added, and again by the
engine: live streams (no `#EXT-X-ENDLIST`, or `type="dynamic"`), which have no end
to download to, and DRM — SAMPLE-AES, FairPlay's `skd://` keys, any
`ContentProtection` — whose pieces cannot be played without a licence. A playlist
pointing a piece or key anywhere but http(s) is refused too; a DASH manifest is
parsed with DTDs prohibited.

**There is no muxer**, so a quality whose sound is separate is saved as two files
side by side — `Talk.mp4` and `Talk.audio.m4a` — both named under the add lock so
neither can replace a file or be claimed by another download, and both removed
with the download when asked. Players (VLC, mpv) open the pair; `ffmpeg -i Talk.mp4
-i Talk.audio.m4a -c copy out.mp4` joins them losslessly.

**From the browser**, the extension notices the playlists a page's player loads
(from the page's resource timeline — no new permission) and counts them on its
toolbar badge; its popup lists them under *Videos on this page* with a
**Download** button that opens Add Download on the playlist, named after the page.

## Credentials

**HTTP Basic** is sent pre-emptively rather than after a `401`: every
connection shares one static `HttpClient`, so `HttpClientHandler.Credentials`
is not available, and presenting the header up front also saves a round trip
on each of up to 32 connections.

Credentials live on the `DownloadItem`, so a download paused for three days
still presents the same login when it resumes. The password is **never written
to `downloads.json` in the clear**: `DownloadCredentials.Password` is
`[JsonIgnore]`d and the serialized `ProtectedPassword` is encrypted with DPAPI
(`SecretProtector`, `CurrentUser` scope, plus an app-specific entropy value).
A profile copied to another machine simply gets an empty password back and is
asked again — decryption failing is not a reason to refuse to start. This is
the one and only NuGet reference in the solution
(`System.Security.Cryptography.ProtectedData`), needed to compile against
`ProtectedData` and deploying nothing at all.

A `user:password@host` URL is split apart the moment it is entered
(`UrlCredentials`), so the secret ends up in the field that encrypts itself
rather than in `DownloadItem.Url`, which is persisted, displayed and logged.

The Add Download window has **no login fields**. Almost no download needs one,
and a checkbox and two boxes on every one of them made the ordinary case look
like it had a decision in it. `CredentialsForm` opens instead when a server
actually asks — on the `AuthenticationRequiredException` the probe raises —
carrying the host and the server's own account of the refusal, and the fetch is
retried with what it collects. There is no "remember me" on it: a download
paused for three days has to present the same login when it resumes, so the
credentials are kept either way, encrypted.

## Command line

`qb.exe` sits beside `QuickByte.exe` (setup can add it to `PATH`) and drives the
running app from a prompt or a script:

```
qb add https://example.com/big.iso                    add and start (QuickByte is started if needed)
qb add URL1 URL2 --queue Night                        file them into a queue (created if missing)
qb add URL -o D:\Isos -n disc.iso -c 16 --checksum sha256:…
qb add https://cdn.example.com/talk/master.m3u8 --quality 720p
qb list [--status active|paused|queued|completed|failed|all] [--json]
qb pause 3f9c | qb resume all
qb queues [--json] | qb queue start Night | qb queue stop Night
qb status [--json]
```

Exit codes: `0` done, `1` QuickByte refused or failed (a link that could not be
added, an unknown id), `2` a usage mistake, `3` QuickByte is not running. `--json`
prints the response itself, camel-cased, for scripts.

- **Nothing is decided in `qb`.** It parses the command line
  (`Core/Commands/CommandLine`, tested), resolves a relative `--folder` against
  its own working directory, and sends the request over a named pipe. The app's
  `CommandProcessor` carries it out on the UI thread through the same managers the
  windows use: the link is probed, named, reserved, filed into a queue or started
  exactly as Add Download would — without opening a details window over whatever
  the user is doing. A streaming video gets the quality `--quality` names
  (`best`, the default; `worst`; or part of a label).
- **A login in a link** (`https://user:pass@host/…`) is lifted into the download's
  encrypted credentials, as in the Add window.
- **Adding work starts QuickByte** (minimized, from beside `qb.exe`, never from
  PATH) when it is not running; reading state (`list`, `status`) just says it is
  not running.
- **The pipe is this user's only.** `QuickByte.Command.<user>` is created and
  opened with `PipeOptions.CurrentUserOnly`: its access list names only the
  current user, and the client checks the server's owner before sending — another
  account can neither add downloads nor impersonate QuickByte to `qb`. Requests are
  capped in size, and a malformed one is answered, not fatal.
- A download's id is the one `qb list` prints; any unambiguous start of it (four
  characters or more) will do.

## Browser integration

`BrowserIntegrationServer` (Core) listens on **127.0.0.1** and answers two
routes: `GET /ping` and `POST /download`. The extension in
`browser/chrome-extension/` — one set of scripts for **Chrome, Edge and
Firefox**, packaged for each by `browser/package-extension.ps1` — cancels a
download the browser is about to start and posts it here instead, along with
the cookies, referrer and user agent the browser would have used — which is usually the only reason a link from behind a login
resolves to a file rather than a sign-in page. `MainForm` opens the Add
Download dialog pre-filled and already fetching.

It is a raw `TcpListener` rather than `HttpListener`: http.sys URL
reservations are an administrator-level concept, and a download manager that
needs an elevated prompt to talk to a browser extension is not shippable. A
loopback socket needs no permission from anyone.

Everything on the machine can reach a loopback port, including every web page
in the browser, so three things guard it:

- **The bind address** is `IPAddress.Loopback`; nothing off-machine connects.
- **A pairing token** must be present as `X-QuickByte-Token` on every request,
  compared in fixed time. It is generated on first use, shown in
  *Options → Browser*, and pasted into the extension's options page. A page
  can send a request whose answer it cannot read, so the secret has to be in
  the request itself.
- **The origin** must be `chrome-extension://` or `moz-extension://` for any
  CORS header to be issued, so an ordinary page's JSON `POST` never survives
  its preflight.

QuickByte's installer offers the extension rather than shipping it: it writes a
Chrome *external extension* registry entry pointing at the Web Store, so Chrome
raises its own "New extension added" prompt at the next launch — the same
mechanism Adobe and IDM use. No app can install a Chrome extension silently
without enterprise policy, and QuickByte deliberately doesn't try.

The extension also lists the video playlists a page's player has loaded, in its
toolbar popup, and sends one on request — see Streaming video.

See `browser/chrome-extension/README.md` for pairing and
`browser/chrome-extension/STORE.md` for publishing.

## Configurable settings

Exposed via **Settings** in the main window toolbar, all settings map
directly to `DownloadSettings`:

| Setting | Range / default |
|---|---|
| Default connections per download | 1, 2, 4, 8, 16, 24 or 32 — default 8 |
| Max retries per connection | default 3 |
| Retry base delay | default 1500 ms (exponential backoff) |
| Max concurrent downloads | default 3 |
| Global speed limit | default 0 (unlimited), in KB/s |
| Progress sampling interval | default 100 ms (clamped to 50–2000) |
| Default download folder | `~/Downloads/QuickByte` |
| Open the details window automatically | on |
| Show the download complete window | on |
| Start QuickByte when Windows starts | off |
| Start minimized to the notification area | off |
| Keep the computer awake while downloads are running | on |
| Browser integration | on |
| Browser bridge port | 1024–65535, default 9614 |
| Browser pairing token | generated on first use |
| Proxy | Windows' settings (default), none, or HTTP / SOCKS5 / SOCKS4 with login and bypass list |

A queue's own settings — downloads at once, queue speed limit, and its
schedule — are per queue rather than global, and live in the Queues & Scheduler
window (`queues.json`) instead of here.

**The chunk folder is not a setting.** In-flight `.tmp` segments live in
`%AppData%/QuickByte/temp`, beside `settings.json`, `downloads.json` and
`queues.json` — derived by `SettingsService`, never persisted, and no longer
offered in Options. It used to be under `%TEMP%`, which is the one directory on
a Windows machine that gets emptied on purpose: by the user, by Disk Cleanup,
and by every "PC cleaner" ever installed. Resume is driven entirely by chunk
length on disk, so a sweep during a pause does not lose a cache, it loses the
download.

Where *finished* files land is unaffected and is still yours to choose — the
**Folders** tab sets the default, and Add Download's **Save to** box overrides
it for a single file.

Three of these are honoured **live** rather than at the next download: the
global speed limit, browser integration (enabling it, or moving its port, takes
effect on Save), and the proxy. Everything else is snapshotted — see `CLAUDE.md`.

### Proxy

The **Proxy** tab chooses how downloads reach the internet: **using Windows'
proxy settings** (the default, and what every earlier version did), **directly**,
or **through a proxy named here** — HTTP, SOCKS5 or SOCKS4, with an optional user
name and password (stored DPAPI-encrypted, like download passwords) and a list of
hosts to reach directly (`example.com; *.lan; .corp.example.com; <local>`, the
forms Windows takes). This machine is never reached through a proxy.

- **Every HTTP client uses it** — downloads, probes, streaming playlists and the
  update check — through one `AppProxy`, which the handlers ask on every request.
  That is what makes a change apply to the next request without a restart; the
  login is looked up at the moment a proxy asks for it, for the same reason.
- **Host names go to a SOCKS proxy to resolve** (SOCKS5 domain addressing, SOCKS4a),
  so DNS lookups do not leak around it.
- **FTP goes through a proxy set here**, control and passive data connections
  alike (`ProxyTunnel`: SOCKS5, SOCKS4a or HTTP `CONNECT`). Windows' own proxy
  settings describe the web, so with them FTP connects directly — and most HTTP
  proxies refuse `CONNECT` to port 21 anyway; SOCKS is the one to use for FTP.
- **A `407` is the proxy's**, and reported as such — "Enter them in Options >
  Proxy" — rather than raising the site's sign-in prompt, which would have the
  user type a site password that then went to the proxy. It is not retried.
- With Windows' settings, a proxy that challenges with NTLM or Negotiate gets the
  signed-in user's credentials, as a browser gives them. A proxy named here gets
  only the login typed for it.

A proxy sees the address of every download, and HTTP and SOCKS proxies receive
their password unencrypted; the tab says so.

The two startup settings are on the **System** tab, beside keeping the computer
awake while downloads run. "Start with Windows"
writes a value under `HKCU\...\CurrentVersion\Run` — per-user, no elevation,
and visible in Task Manager's Startup tab so it can be turned off from there
too; QuickByte re-asserts it at each launch, which is what keeps it pointing at
the right executable after an update. "Start minimized" applies to every
launch, not only the one Windows performs at sign-in, and `--minimized` on the
command line forces it for a single run.

## UI

**Light and dark.** *Options > Interface > Appearance* chooses a light or a
dark palette, or follows Windows' own app mode (the default). Every colour comes
from `Theme`, which holds both palettes and is set once at startup — a WinForms
window takes its colours when it is built, so a change applies at the next
launch. In dark mode each window also gets a dark title bar and dark scroll
bars, list headers and drop-downs, which WinForms cannot reach on its own
(`DarkMode`, the one place that asks Windows for them). The contrast of every
text/background pair that matters is checked against WCAG ratios in the tests,
so a later tweak cannot make the dark theme quietly unreadable.

Every window is styled after classic IDM, on a single flat palette defined in
`UI/Controls/Theme.cs`:

- **Main window** — menu bar (File/Tasks/Downloads/View/Help), an icon
  toolbar (Add URL, Resume, Pause, Stop All, Delete, Delete Completed,
  Properties, Queues, Options), a category tree sidebar (All Downloads,
  grouped by file type, plus Unfinished/Finished/Failed and a Queues branch
  with one node per queue), a downloads grid with a
  per-file-type icon and an owner-drawn progress bar, a right-click context
  menu on each row, and a status bar showing download counts and aggregate
  speed.
- **Download details window** — opens automatically when a download starts.
  Three tabs (Download status, **Speed Limiter**, **Options on completion** — open
  the folder, play a sound, and exit / sleep / hibernate / shut down). The
  Speed Limiter tab caps this download in KB/s and applies the moment it is
  changed, including to a transfer already in flight. The
  Download status tab shows the file/URL header, the
  status/size/downloaded/speed/ETA/connections/resumable fields, an overall
  progress bar, a collapsible "Hide details" section, the segmented "start
  positions and download progress by connections" bar, and a connections grid
  (#, Downloaded, Progress, Info). Its **taskbar button carries the same
  progress** — green while transferring, yellow when paused, red on failure, and
  a marquee while connecting or when the server never gave a size — so a
  minimised download can still be read at a glance.
- **Download complete window** — replaces the details window when a transfer
  finishes: final size, average speed, destination folder, and Open File /
  Open Folder actions. Opting out of it from the checkbox is persisted.
- **Add New Download** shows a **Quality** row when the address is a streaming
  video's playlist — see Streaming video.
- **Add New Download** pre-fills from the clipboard and resolves file
  info (type, size, resumability) before you commit. Its **Save to** box shows a
  folder inside your profile as `~\Downloads\QuickByte` — the full path is wider
  than the box, and the half that gets clipped is the half that says where the
  file is going. A folder anywhere else is shown in full, because that is
  somewhere you chose on purpose (`Core/Helpers/UserPath.cs`). **Options** groups
  settings into Connection / Folders / Interface / Startup / Browser tabs, and
  its *Default download folder* is abbreviated the same way.
- **Batch Download** (*Tasks > Add Batch Download*) — adds many downloads at
  once. Paste a list of links or a whole page's source, import a `.txt` or
  `.html` file, or type a pattern: `https://example.com/photo[001-250].jpg` is
  250 links padded to three digits, `disc[a-c].iso` three, and several ranges
  in one address multiply (`BatchLinks`). Only a range of two numbers or two
  single letters counts, so an IPv6 host like `http://[::1]/` is left alone;
  trailing sentence punctuation and `&amp;` from page sources are cleaned up;
  and a batch is capped at 5,000 links so a typo in a range cannot freeze the
  window. Every link is probed, six at a time, and listed with its real name
  and size; a link that fails (or asks for a login) is left unticked. The batch
  goes into a new queue by default, an existing one, or starts at once — and
  opens no details window per download.

  Adding many downloads at once exposed an old collision: a download's name was
  only checked against files on disk, so two unfinished downloads of
  `download.php` were given the same path and the second to finish replaced
  the first. `DownloadManager.AddDownloadAsync` now also steps past any name an
  unfinished download has claimed, under a lock.
- **Clipboard watching** (*Options > Interface*, off by default) — copy a link to
  a file type on your list (`zip rar 7z iso exe msi mp4 …`, editable) and a
  notification offers the download; clicking it opens Add Download. A
  notification rather than a window, because you were in the middle of copying
  something and a window taking focus would catch your next keystrokes. Only a
  clipboard holding exactly one link and nothing else is looked at
  (`ClipboardLinks`), nothing is kept, a link that is already a download is not
  offered back, and nothing is listening at all until the option is on. It uses
  Windows' clipboard change notification rather than polling, which would hold
  the clipboard open and get in other programs' way.
- **Category folders** (*Options > Folders*, off by default) — sort finished
  files by kind: videos into `…\QuickByte\Video`, installers into `…\Programs`,
  or into a folder chosen per category. Anything of no known kind stays in the
  default folder. Add Download's and Batch Download's *Save to* follow the file
  into its category folder until you change the box yourself. A chosen folder
  that is not a full path — which only a hand-edited `settings.json` could
  produce — is ignored rather than resolved against wherever the app started
  (`DownloadSettings.FolderFor`).
- **Checksums** — paste the SHA-256 (or SHA-512, SHA-384, SHA-1, MD5) from a
  download page into Add Download's optional *Checksum* box, and the finished
  file is checked before the download counts as complete: a new *Verifying*
  step after the merge. A match is shown in the list and the completion window
  (`✓ SHA-256 verified`); a mismatch **fails** the download with a message
  saying so, so a damaged or substituted file never opens from a "Download
  complete" window or sets off the shutdown queued behind it. The file is left
  where it is, to look at. A checksum found later can be checked from the row's
  *Verify Checksum…* or the completion window. It reads the forms download
  pages list — bare hex (the length says which hash), `sha256:…`, a
  `sha256sum` line with the file name after it, BSD's `SHA256 (file) = …` — and
  says plainly that MD5 and SHA-1 prove a file arrived intact but not that
  nobody forged it (`FileChecksum`).
- **Search** — the box at the right of the toolbar (Ctrl+F) filters the list
  as you type, on top of whatever the sidebar has selected. Every word has to
  appear in the file name or the address's host and path, in any order and
  case (`DownloadSearch`); a signed link's query string is left out, since its
  random tokens would match almost anything. Enter or Down moves into the
  results, Esc clears. Ctrl+N — promised by the Add URL tooltip all along —
  now opens Add Download.
- **Sign In** — a small modal that appears only when a server answers a probe
  with a `401` or an FTP `530`. It names the host, repeats what the server
  said, and hands the login straight back to the fetch that was refused.

Every secondary window — Add New Download, download details, download
complete — is a window in its own right: unowned, with its own taskbar button,
and modeless. Opening one from the notification area or from a browser capture
brings up that window alone and leaves the main window wherever it was.
"Close to tray" is still one gesture for the whole application: it hides the
open secondary windows too, and puts the same set back when the main window
returns.
- **Queues & Scheduler window** — every queue on the left; on the right the
  selected queue's files in run order (move up/down/take out), its Options
  (name, downloads at once, queue speed limit) and its Schedule (days, start
  time, optional stop time), with a line saying when it will next start and
  whether the background scheduler is there to do it. Start and Stop run the
  queue by hand. See Queues and scheduling above.
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
- HTTP authentication schemes beyond Basic (Digest, NTLM, negotiate).
- Muxing a stream's separate sound back into its video, DRM-protected and live
  streams, and DASH manifests with more than one period (only the first is read).
- A packed, store-published browser extension — the one in `browser/` is
  loaded unpacked, and only Chrome's hooks are wired.
- Automated tests (the interface-driven design makes the Core layer easy
  to unit test with fakes — `IConnectionFactory` and `IDownloadConnection`
  in particular are there specifically to make the pool manager testable
  without real network calls).
