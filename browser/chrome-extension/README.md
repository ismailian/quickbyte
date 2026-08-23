# QuickByte Integration (Chrome extension)

Takes downloads over from Chrome and hands them to QuickByte, the way IDM's
browser integration does. Clicking a direct download link opens QuickByte's Add
Download window instead of dropping the file into Chrome's downloads folder.

## Install

Once the extension is published, QuickByte's installer offers it for you: it
writes a registry entry that makes Chrome show its own **"New extension added"**
prompt the next time the browser starts. There is no way for an app to install a
Chrome extension silently, and that is deliberate — the prompt is the honest
version of "auto install". See `STORE.md` for publishing and for why every other
route is closed.

**Developing on it**, or running before it is published:

1. Open `chrome://extensions` — you have to type it; Chrome blocks apps from
   opening that page for you.
2. Turn on **Developer mode** (top right).
3. **Load unpacked** → select this folder (`browser/chrome-extension`).

Chrome ignores `--load-extension` from the command line as of Chrome 137, so
Load unpacked is the only way in. Chrome will also periodically offer to disable
developer-mode extensions; that nag goes away once it is installed from the
store.

## Pair it with QuickByte

The extension talks to QuickByte over a socket on `127.0.0.1`. Every process on
the machine can reach that port, so QuickByte only accepts requests carrying a
secret the two sides share. Carrying it across is the one manual step:

1. In QuickByte: **Tasks → Options → Browser**. Copy the **pairing token**, and
   note the port (9614 unless you changed it).
2. In Chrome: the QuickByte toolbar icon → **Options…**. Paste the token and
   press **Test connection** — it should report *Connected*.

Test connection saves before it tests, and the token and port also save when you
tab out of them, so there is no order to get wrong. The **Save** button at the
bottom is for the capture filters below.

**New token…** in QuickByte's Browser options issues a fresh secret and unpairs
every browser using the old one.

## What it captures

Two hooks, catching different things:

- **Chrome's download hook** (`downloads.onDeterminingFilename`) sees every
  download the browser starts, including ones no link click produced. It fires
  before the file is created, so cancelling there leaves nothing behind.
- **A link-click hook** (`content.js`) takes a plain left-click on a direct
  download link before Chrome starts a download at all. It stops the click
  first and asks QuickByte afterwards — and if QuickByte is not running, the
  navigation is put back, so the browser downloads the file itself a moment
  later exactly as it would have.

There is also a **Download with QuickByte** entry on the right-click menu for
links, images, video and audio. That one ignores the file-type filter: an
explicit menu click is you overruling the filter, not asking for it.

Alongside the URL, the extension sends the **cookies, referrer and user agent**
Chrome would have used. Without them a link from behind a login frequently
resolves to a sign-in page in any other program — the hand-off appears to work
and downloads the wrong bytes.

## Options

Settings live in `chrome.storage.local`, not `storage.sync`. The pairing token
belongs to one QuickByte install on one machine: syncing it to a second computer
would carry a secret the QuickByte over there does not accept, and overwrite the
one that works. A pairing made by an earlier build is carried over from `sync`
automatically, once.

| Option | Default | Notes |
|---|---|---|
| Send downloads to QuickByte | on | Master switch, also on the toolbar popup |
| Pairing token | *(empty)* | From QuickByte → Options → Browser |
| Bridge port | 9614 | Must match QuickByte's setting |
| What to take over | only listed file types | The alternative is every download |
| File types | archives, disc images, installers, media | Extensions, comma or space separated |
| Never capture from these sites | *(empty)* | One host per line; covers subdomains |

The default is deliberately **not** "every download": a freshly installed
extension that silently swallows the one-click PDF you expected to open in a
tab reads as broken rather than as installed.

## Permissions, and why each is needed

| Permission | Why |
|---|---|
| `downloads` | To see a download starting, and to cancel the one QuickByte takes over |
| `cookies` | To send the session cookies the link needs to resolve |
| `storage` | To keep the token and settings on this machine |
| `contextMenus` | The "Download with QuickByte" entry |
| `http://127.0.0.1/*` | To reach QuickByte's bridge |
| `<all_urls>` | Required for reading cookies on the site a download came from, and for the link-click hook |

## If it isn't working

- **"Not connected"** — QuickByte isn't running, browser integration is off in
  its Options, or the port doesn't match. QuickByte's *Options → Browser* shows
  what it is actually listening on, and why if it failed (usually the port
  already being in use).
- **"QuickByte rejected the token"** — copy it again; **New token** invalidates
  the old one.
- **"The token box is empty"** — the paste didn't land in the field. Click into
  the box, paste, and press Test connection again.
- **Downloads still go to Chrome** — the file's extension probably isn't in the
  list. Add it, or switch to *Every download Chrome starts*.
