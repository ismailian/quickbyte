// Background script (a service worker in Chrome and Edge, an event page in
// Firefox): decides which downloads QuickByte should take over, gathers the
// request context the browser would have used, and posts it to the loopback
// bridge (BrowserIntegrationServer in QuickByte.Core).
//
// Two capture paths, because they catch different things:
//
//   * onDeterminingFilename — every download Chrome starts, including the ones
//     no link click produced (a redirect, a script-triggered save). It fires
//     before the file is created, so cancelling there leaves nothing on disk.
//
//   * a message from content.js — a plain link click, taken over before Chrome
//     starts a download at all. Redundant with the above for most files, but it
//     is the only path with no Chrome download record in it whatsoever.
//
// The order of operations matters in both: QuickByte is asked *first*, and the
// Chrome download is only cancelled once it has accepted. A bridge that is
// down (QuickByte not running) must leave the browser's own download alone.

// Chrome's service worker loads the shared file itself. Firefox has no
// importScripts in a background script; its manifest lists shared.js first.
if (typeof importScripts === "function") importScripts("shared.js");

/** Anything slower than this and Chrome is left waiting on a filename. */
const BRIDGE_TIMEOUT_MS = 3000;

// ---------------------------------------------------------------- bridge --

async function bridgeRequest(path, body) {
  const settings = await qbGetSettings();
  if (!settings.token) throw new Error("not paired");

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), BRIDGE_TIMEOUT_MS);

  try {
    const response = await fetch(qbBridgeUrl(settings, path), {
      method: body ? "POST" : "GET",
      headers: {
        "Content-Type": "application/json",
        "X-QuickByte-Token": settings.token
      },
      body: body ? JSON.stringify(body) : undefined,
      signal: controller.signal
    });

    if (response.status === 401) throw new Error("pairing token rejected");
    if (!response.ok) throw new Error(`bridge returned ${response.status}`);
    return await response.json();
  } finally {
    clearTimeout(timer);
  }
}

/**
 * The cookies Chrome would have sent, flattened into one header. Without these
 * a link from behind a login resolves to a sign-in page in QuickByte, which is
 * the single most common way a hand-off "works" but downloads the wrong bytes.
 */
async function cookieHeaderFor(url) {
  try {
    const cookies = await qbApi.cookies.getAll({ url });
    return cookies.map((cookie) => `${cookie.name}=${cookie.value}`).join("; ");
  } catch {
    return ""; // No host permission for this site, or an opaque origin.
  }
}

async function sendToQuickByte({ url, fileName, fileSize, mimeType, referrer }) {
  const payload = {
    url,
    fileName: fileName || "",
    fileSize: fileSize > 0 ? fileSize : 0,
    mimeType: mimeType || "",
    referrer: referrer || "",
    userAgent: navigator.userAgent,
    cookie: await cookieHeaderFor(url)
  };

  await bridgeRequest("/download", payload);
  flashBadge("↓", "#209856");
}

// ------------------------------------------------------------ indicators --

let badgeTimer = null;

/**
 * A two-second badge instead of a notification: taking a download over is a
 * confirmation, not news, and QuickByte's own window is about to open anyway.
 */
function flashBadge(text, color) {
  qbApi.action.setBadgeBackgroundColor({ color });
  qbApi.action.setBadgeText({ text });

  clearTimeout(badgeTimer);
  badgeTimer = setTimeout(() => qbApi.action.setBadgeText({ text: "" }), 2000);
}

function reportFailure(error) {
  console.warn("QuickByte hand-off failed:", error);
  flashBadge("!", "#CD4444");
}

// -------------------------------------------------------- download hook --

/** Strips the directory part Chrome puts in suggested filenames. */
function baseName(path) {
  if (!path) return "";
  const cut = Math.max(path.lastIndexOf("/"), path.lastIndexOf("\\"));
  return cut >= 0 ? path.slice(cut + 1) : path;
}

async function takeOverChromeDownload(item) {
  const settings = await qbGetSettings();

  // finalUrl is the one after redirects, which is what QuickByte should probe;
  // it is absent on older Chrome, hence the fallback.
  const url = item.finalUrl || item.url;
  const fileName = baseName(item.filename);

  if (!qbShouldCapture(settings, url, fileName)) return false;

  await sendToQuickByte({
    url,
    fileName,
    fileSize: item.fileSize || item.totalBytes || 0,
    mimeType: item.mime,
    referrer: item.referrer
  });

  return true;
}

async function cancelChromeDownload(id) {
  try {
    await qbApi.downloads.cancel(id);
  } catch {
    // Already finished or already gone. Nothing to undo.
  }

  try {
    await qbApi.downloads.erase({ id });
  } catch {
    // Leaving the row in the download list is cosmetic; not worth reporting.
  }
}

if (qbApi.downloads.onDeterminingFilename) {
  qbApi.downloads.onDeterminingFilename.addListener((item, suggest) => {
    (async () => {
      try {
        if (await takeOverChromeDownload(item)) await cancelChromeDownload(item.id);
      } catch (error) {
        // Deliberately swallowed into a badge: QuickByte may simply not be
        // running, and that must cost the user nothing more than Chrome
        // downloading the file itself, exactly as it would without us.
        reportFailure(error);
      }

      // Always called, cancelled or not. Chrome blocks the download until the
      // listener answers, and on a cancelled one the call is a no-op.
      suggest();
    })();

    return true; // suggest() is called asynchronously.
  });
} else {
  // Firefox and friends have no onDeterminingFilename. onCreated fires earlier
  // and usually without a filename, so the hand-off leans on QuickByte's own
  // probe to name the file.
  qbApi.downloads.onCreated.addListener(async (item) => {
    try {
      if (await takeOverChromeDownload(item)) await cancelChromeDownload(item.id);
    } catch (error) {
      reportFailure(error);
    }
  });
}

// ----------------------------------------------------------- content hook --

qbApi.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (typeof message?.type === "string" && message.type.includes("stream")) {
    return handleStreamMessage(message, sender, sendResponse) ?? false;
  }

  if (message?.type === "qb-capture") {
    sendToQuickByte({
      url: message.url,
      fileName: message.fileName,
      fileSize: 0,
      mimeType: "",
      referrer: message.referrer || sender.tab?.url || ""
    })
      .then(() => sendResponse({ ok: true }))
      .catch((error) => {
        reportFailure(error);
        // The content script prevented the navigation before asking, so a
        // failure here has to be reported rather than swallowed: it is the
        // signal that tells the page to let the click happen after all.
        sendResponse({ ok: false, error: String(error.message || error) });
      });

    return true; // async sendResponse
  }

  if (message?.type === "qb-ping") {
    bridgeRequest("/ping")
      .then((info) => sendResponse({ ok: true, info }))
      .catch((error) => sendResponse({ ok: false, error: String(error.message || error) }));

    return true;
  }

  return false;
});

// ---------------------------------------------------------------- streams --
//
// The playlists each tab's video players have loaded, as content.js reports them.
// Kept in storage.session — memory only, never written to disk, gone with the
// browser — because a service worker is stopped whenever it is idle and would
// forget a plain variable. Nothing here leaves the browser until the user picks a
// video in the popup.

const streamKey = (tabId) => `streams:${tabId}`;

/** Only the part of a tab's address that names the page: a new #fragment is not a new page. */
function pageOf(url) {
  return String(url || "").split("#")[0];
}

async function streamsFor(tabId) {
  const key = streamKey(tabId);
  const stored = await qbApi.storage.session.get(key);
  return stored[key] || { page: "", urls: [] };
}

async function showStreamCount(tabId, count) {
  try {
    await qbApi.action.setBadgeBackgroundColor({ tabId, color: "#0072C6" });
    await qbApi.action.setBadgeText({ tabId, text: count > 0 ? String(count) : "" });
  } catch {
    // The tab closed in the meantime.
  }
}

async function rememberStreams(tab, urls) {
  const record = await streamsFor(tab.id);
  const page = pageOf(tab.url);

  // Another page in the same tab starts a list of its own.
  let list = record.page === page ? record.urls : [];
  for (const url of urls) list = qbRememberStream(list, url);

  await qbApi.storage.session.set({ [streamKey(tab.id)]: { page, urls: list } });
  await showStreamCount(tab.id, list.length);
}

async function forgetStreams(tabId) {
  try {
    await qbApi.storage.session.remove(streamKey(tabId));
  } catch {
    // Nothing was stored.
  }
  await showStreamCount(tabId, 0);
}

// Every frame of a page reports on its own, often at once; one write at a time,
// or two frames reading the same list would each drop the other's playlist.
let streamWrites = Promise.resolve();

function queueStreamWrite(work) {
  streamWrites = streamWrites.then(work).catch((error) => console.warn("QuickByte stream list:", error));
  return streamWrites;
}

qbApi.tabs.onRemoved.addListener((tabId) => {
  queueStreamWrite(() => forgetStreams(tabId));
});

qbApi.tabs.onUpdated.addListener((tabId, changeInfo) => {
  if (!changeInfo.url) return;
  queueStreamWrite(async () => {
    const record = await streamsFor(tabId);
    if (record.page && record.page !== pageOf(changeInfo.url)) await forgetStreams(tabId);
  });
});

function handleStreamMessage(message, sender, sendResponse) {
  if (message.type === "qb-streams") {
    const urls = (Array.isArray(message.urls) ? message.urls : []).filter(qbIsStreamManifest);
    if (sender.tab?.id !== undefined && urls.length > 0) queueStreamWrite(() => rememberStreams(sender.tab, urls));
    return false;
  }

  if (message.type === "qb-list-streams") {
    queueStreamWrite(async () => {
      const record = await streamsFor(message.tabId);
      sendResponse({ urls: record.page === pageOf(message.page) ? record.urls : [] });
    });
    return true;
  }

  if (message.type === "qb-send-stream") {
    // Held to the same rule as a report: the popup only ever lists playlists.
    if (!qbIsStreamManifest(message.url)) {
      sendResponse({ ok: false, error: "not a video playlist" });
      return false;
    }

    sendToQuickByte({
      url: message.url,
      fileName: qbStreamFileName(message.title, message.url),
      fileSize: 0,
      mimeType: "",
      referrer: message.page || ""
    })
      .then(() => sendResponse({ ok: true }))
      .catch((error) => {
        reportFailure(error);
        sendResponse({ ok: false, error: String(error.message || error) });
      });
    return true;
  }

  return undefined;
}

// ------------------------------------------------------------ context menu --

qbApi.runtime.onInstalled.addListener(() => {
  qbApi.contextMenus.create({
    id: "quickbyte-download",
    title: "Download with QuickByte",
    contexts: ["link", "image", "video", "audio"]
  });
});

qbApi.contextMenus.onClicked.addListener((info, tab) => {
  // srcUrl before linkUrl for media: right-clicking a <video> inside an <a>
  // should offer the video, not the page it links to.
  const url = info.linkUrl || info.srcUrl;
  if (!url) return;

  // Deliberately bypasses qbShouldCapture: an explicit menu click is the user
  // overruling the file-type filter, not asking it to be applied.
  sendToQuickByte({ url, fileName: "", fileSize: 0, mimeType: "", referrer: info.pageUrl || tab?.url || "" })
    .catch(reportFailure);
});
