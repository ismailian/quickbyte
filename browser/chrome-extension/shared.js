// Settings and URL rules shared by the service worker, the options page and the
// popup. Loaded with importScripts() in the worker and with a <script> tag in
// the two pages, so everything here has to be plain globals — no modules.

const QB_DEFAULTS = {
  enabled: true,
  port: 9614,
  token: "",

  // "all" takes over every download Chrome starts; "types" limits it to the
  // extensions below. Default is "types" on purpose: a first-run extension that
  // silently swallows *every* download, including the one-click PDF the user
  // expected to open in a tab, reads as broken rather than as installed.
  capture: "types",

  fileTypes: [
    "7z", "apk", "appx", "avi", "bin", "bz2", "deb", "dmg", "exe", "flac", "flv",
    "gz", "img", "iso", "jar", "mkv", "mov", "mp3", "mp4", "msi", "msix", "pkg",
    "rar", "rpm", "tar", "tgz", "wav", "webm", "wmv", "xz", "zip", "zst"
  ].join(", "),

  // Hosts the extension keeps its hands off entirely, one per line. Anything
  // whose download is really a short-lived signed URL tied to the page session
  // belongs here.
  excluded: ""
};

// storage.local, deliberately, not storage.sync. The pairing token belongs to
// one QuickByte install on one machine — syncing it to a second computer would
// carry a secret that the QuickByte over there does not accept, and quietly
// replace the one that works. storage.local also cannot fail the way sync can
// (write quotas, disabled-by-policy), which matters for the one setting the
// extension is useless without.
const QB_STORE = "local";

async function qbGetSettings() {
  const stored = await chrome.storage.local.get(QB_DEFAULTS);
  if (stored.token) return { ...QB_DEFAULTS, ...stored };

  // Carry over a pairing done by an earlier build, which used storage.sync.
  // Only when local has nothing, and only once — after this the copy in local
  // is the one that answers.
  try {
    const synced = await chrome.storage.sync.get(QB_DEFAULTS);
    if (synced.token) {
      await chrome.storage.local.set(synced);
      return { ...QB_DEFAULTS, ...synced };
    }
  } catch {
    // sync unavailable; there is simply nothing to carry over.
  }

  return { ...QB_DEFAULTS, ...stored };
}

async function qbSaveSettings(values) {
  await chrome.storage.local.set(values);
}

function qbBridgeUrl(settings, path) {
  return `http://127.0.0.1:${settings.port}${path}`;
}

/** Extensions as a lower-cased set, tolerating "zip, .rar; iso" style input. */
function qbFileTypeSet(settings) {
  return new Set(
    String(settings.fileTypes || "")
      .split(/[\s,;]+/)
      .map((type) => type.trim().toLowerCase().replace(/^\./, ""))
      .filter(Boolean)
  );
}

function qbExcludedHosts(settings) {
  return String(settings.excluded || "")
    .split(/[\s,;\n]+/)
    .map((host) => host.trim().toLowerCase())
    .filter(Boolean);
}

function qbIsExcluded(settings, url) {
  let host;
  try {
    host = new URL(url).hostname.toLowerCase();
  } catch {
    return true; // Unparseable, so not something to hand on.
  }

  // Suffix match so one entry covers a site's subdomains, with a dot guard so
  // "example.com" does not also silence "notexample.com".
  return qbExcludedHosts(settings).some(
    (excluded) => host === excluded || host.endsWith("." + excluded)
  );
}

/** The extension of the file a URL points at, ignoring query and fragment. */
function qbExtensionOf(url) {
  try {
    const path = new URL(url).pathname;
    const name = path.slice(path.lastIndexOf("/") + 1);
    const dot = name.lastIndexOf(".");
    return dot > 0 ? name.slice(dot + 1).toLowerCase() : "";
  } catch {
    return "";
  }
}

/**
 * Whether a URL is one QuickByte should take over, given the current mode. The
 * scheme test is not a formality: blob: and data: URLs exist only inside the
 * page that made them, so handing one to another process downloads nothing.
 */
function qbShouldCapture(settings, url, fileName) {
  if (!settings.enabled) return false;
  if (!/^(https?|ftps?):/i.test(url)) return false;
  if (qbIsExcluded(settings, url)) return false;
  if (settings.capture === "all") return true;

  const types = qbFileTypeSet(settings);
  if (types.has(qbExtensionOf(url))) return true;

  // Chrome's own suggested name is checked too: a download URL is often an
  // opaque /files/8fd21ac3 with the real name only in Content-Disposition.
  if (fileName) {
    const dot = String(fileName).lastIndexOf(".");
    if (dot > 0 && types.has(fileName.slice(dot + 1).toLowerCase())) return true;
  }

  return false;
}
