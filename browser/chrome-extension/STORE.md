# Chrome Web Store submission

Everything needed to publish **QuickByte Integration** and switch on the
install prompt. The prompt is the whole reason for publishing: Windows Chrome
refuses to install extensions that are not in the store, so a store listing is
what makes the registry hand-off in `QuickByte App Packager.iss` legal.

## Why publishing is the only route to an install prompt

| Approach | Status in Chrome 151 |
|---|---|
| `chrome.webstore.install()` inline install | Removed in Chrome 71 |
| Registry `Extensions` key with a local `path` to a `.crx` | Installs, then Chrome force-disables it **and greys out the toggle** — see below |
| Registry `Extensions` key with the store's `update_url` | **Works.** Chrome prompts on next launch |
| `--load-extension` on the command line | Ignored since Chrome 137 |
| `ExtensionInstallForcelist` / `ExtensionSettings` policy | Works, but needs admin, brands the browser "Managed by your organisation", and the user cannot remove the extension |

Adobe Acrobat uses the third row, and that is the row this extension targets.

### Why packaging a .crx does not help

The obvious idea — pack the extension, ship it with the installer, let the user
click Install — was tried against Chrome 151 rather than assumed. Chrome packs
the `.crx` happily (`chrome.exe --pack-extension=<dir>`) and the registry entry
does install it. What the user then gets is:

```
toggleExists   : true
toggleChecked  : false
toggleDisabled : true          <- the user cannot turn it on
```

with the card reading:

> Not from Chrome Web Store. … This extension is not listed in the Chrome Web
> Store and may have been added without your knowledge. … This extension may
> have been corrupted. Turn on developer mode to use this extension …

and a Safety check panel at the top of the page: *"Review one extension that may
be unsafe — Chrome recommends that you remove it."* The stored
`disable_reasons` is `256`, Chrome's corrupted/off-store bucket.

So there is no "click Install" to offer. The button is removed, the only escape
hatch is Developer mode, and Chrome actively recommends uninstalling the app's
own extension. Being in the store is what makes the extension enable-able at
all — the prompt is a consequence of that, not the point of it.

## One-time setup

1. A Chrome Web Store developer account — <https://chrome.google.com/webstore/devconsole>.
   One-time US$5 registration fee.
2. Verify the publisher e-mail and, if the listing should say "quickbyte.ismailaatif.com",
   verify that domain in the account so the listing is not marked as coming from an
   unverified publisher.

## Building the package

```powershell
.\browser\package-extension.ps1
```

Writes `browser/dist/quickbyte-integration-<version>.zip` with the manifest at
the archive root — the store rejects an archive whose manifest sits one folder
down — and with the repo's own `README.md`/`STORE.md` left out.

Bump `version` in `manifest.json` before every upload. The store refuses a
version it has already seen, and it will not accept a lower one afterwards.

### Icons

`icons/*.png` are generated from `UI/Controls/BrandIcon.cs`, the same drawing
code behind the .exe's Win32 icon and the title-bar icon, so the extension's
icon can never drift from the app's. They are checked in; regenerate only after
changing `BrandIcon.Draw`:

```csharp
foreach (int size in new[] { 16, 32, 48, 128 })
    BrandIcon.CreateBitmap(size).Save($"icons/icon{size}.png", ImageFormat.Png);
```

## Listing copy

**Name** — QuickByte Integration

**Summary** (132 characters max)

> Takes downloads over from Chrome and hands them to the QuickByte download manager.

**Category** — Workflow & Planning · **Language** — English

**Description**

> QuickByte Integration hands your downloads to QuickByte, a multi-connection
> download manager for Windows, instead of letting Chrome fetch them one stream
> at a time.
>
> • Click a download link and QuickByte's Add Download window opens, already
>   filled in and checking the file.
> • Splits each download across up to 32 parallel connections, with pause,
>   resume and retry.
> • Sends the cookies and referrer of the page the link came from, so files
>   behind a sign-in download correctly rather than saving the login page.
> • Right-click any link, image, video or audio and choose
>   "Download with QuickByte".
> • Choose which file types to take over, or take over everything, and exclude
>   sites you would rather Chrome kept handling.
>
> This extension does nothing on its own — it needs QuickByte installed and
> running on the same computer. It talks to it over 127.0.0.1 only, and pairs
> with a token you copy from QuickByte's own settings, so nothing else on the
> machine can drive it.
>
> QuickByte is free. Get it at https://quickbyte.ismailaatif.com/

## Single purpose

The store requires one sentence, and a listing that reads as two products in a
trenchcoat is a rejection:

> The extension's single purpose is to redirect downloads initiated in Chrome to
> the QuickByte download manager running on the same computer.

## Permission justifications

Paste these into the dashboard's "Privacy practices" tab. Every field is
mandatory and a vague answer is the most common reason a review stalls.

| Field | Justification |
|---|---|
| `downloads` | The extension's entire function is to take a download over. It needs this to be notified when Chrome starts one, and to cancel the Chrome-side download once QuickByte has accepted the hand-off. Without it there is nothing to intercept. |
| `cookies` | A download link on a signed-in page usually only resolves for that session. The extension reads the cookies Chrome would itself have sent for that exact URL and passes them to QuickByte, so the file downloads instead of a sign-in page. Cookies are read for the download's own URL only, at the moment of hand-off, and are sent only to 127.0.0.1. |
| `storage` | Stores the user's settings on this machine: the pairing token for the local QuickByte install, its port, and which file types to take over. `chrome.storage.local`, never `sync` — the token is specific to one QuickByte install on one computer. |
| `contextMenus` | Adds a single "Download with QuickByte" entry to the right-click menu for links, images, video and audio. |
| `http://*/*`, `https://*/*` (host permissions) | Two needs, both unavoidable for a general-purpose download manager: reading the cookies for whatever site a download came from, and running a small content script that catches a click on a download link before Chrome begins fetching it. The user chooses which sites to exclude in the options page. |
| Remote code | None. The extension loads no remote script; every file is in the package. |

## Data-use disclosures

The dashboard asks what is collected. The honest answers are unusually good
here, and saying so plainly is what gets a `cookies` request through review:

- **Personally identifiable information** — not collected.
- **Authentication information** — *handled, not collected.* Cookies for the
  download's URL are read and forwarded to QuickByte on `127.0.0.1`. They are
  never sent off the machine, never stored by the extension, and never sent to
  the developer or a third party.
- **Web history** — not collected. The extension acts on the URL of a download
  as it starts and keeps no record of it.
- **User activity / location / health / financial** — not collected.

Tick all three certifications: no sale of data, no use unrelated to the single
purpose, no use to determine creditworthiness.

## After it is published

The store issues a permanent 32-character extension ID. That ID is what turns
the install prompt on:

1. Open `QuickByte App Packager.iss` and set:

   ```
   #define ChromeExtensionId "the-id-the-store-gave-you"
   ```

   The `[Registry]` section is written to be inert until that is filled in, so
   nothing happens by accident before there is a real extension behind it.

   The `.iss` is **gitignored**, so if it is ever regenerated the block below
   goes with it. This is the whole of it:

   ```
   [Tasks]
   Name: "browserintegration"; Description: "Offer the QuickByte extension in Chrome and Edge"; GroupDescription: "Browser integration:"

   [Registry]
   Root: HKA; Subkey: "Software\Google\Chrome\Extensions\{#ChromeExtensionId}"; ValueType: string; ValueName: "update_url"; ValueData: "{#ChromeWebStoreUpdateUrl}"; Flags: uninsdeletekey; Tasks: browserintegration
   Root: HKA; Subkey: "Software\Microsoft\Edge\Extensions\{#ChromeExtensionId}"; ValueType: string; ValueName: "update_url"; ValueData: "{#ChromeWebStoreUpdateUrl}"; Flags: uninsdeletekey; Tasks: browserintegration
   ```

   `HKA` follows the install mode — HKLM for an all-users install, HKCU for a
   per-user one. `uninsdeletekey` matters more than it looks: leave the key
   behind and Chrome keeps re-offering an extension for an app that is no longer
   installed.

2. Bump the version in `Directory.Build.props` **and** in the `.iss`, rebuild
   Release, and recompile the installer — the ordinary release ritual in
   `CLAUDE.md`. The registry entries are the user-visible change that release
   is shipping.

3. Install it and launch Chrome. Chrome shows its own "New extension added"
   prompt on that launch, not at install time — the two are separate events and
   the prompt does not appear until the browser next starts.

4. Update `browser/chrome-extension/README.md` to point at the store listing
   instead of the Load-unpacked instructions.
