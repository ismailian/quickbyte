// Catches a click on a direct download link and hands it to QuickByte before
// Chrome starts a download of its own.
//
// The service worker's download hook already catches everything this does, one
// step later. This exists for the step it saves: no Chrome download record is
// ever created, so there is no row to cancel and erase, and the QuickByte window
// opens on the click rather than after the browser has decided what to do.
//
// preventDefault() has to happen synchronously — by the time the worker answers,
// the navigation is long gone — so the click is stopped first and the answer
// is waited for afterwards. If the hand-off fails (QuickByte not running, wrong
// token), the navigation is put back exactly as it was. That fallback is the
// whole reason this is safe: the worst case is the browser downloading the file
// itself, a moment later than it would have.

let settings = null;

qbGetSettings().then((loaded) => { settings = loaded; });

chrome.storage.onChanged.addListener((changes, area) => {
  // Only the area qbGetSettings reads; a stale sync entry from an earlier build
  // must not overwrite what the options page just wrote.
  if (area !== QB_STORE || !settings) return;
  for (const [key, change] of Object.entries(changes)) settings[key] = change.newValue;
});

/**
 * True only for a click that would navigate. Middle clicks, Ctrl/Shift/Alt
 * clicks and anything a page has already handled belong to the browser.
 */
function isPlainClick(event) {
  return (
    event.button === 0 &&
    !event.defaultPrevented &&
    !event.ctrlKey &&
    !event.metaKey &&
    !event.shiftKey &&
    !event.altKey
  );
}

function downloadLinkFrom(target) {
  const anchor = target.closest?.("a[href]");
  if (!anchor) return null;

  const url = anchor.href;
  if (!url) return null;

  // A download attribute is the page stating outright that this is a file, so
  // it counts regardless of the extension filter — which is exactly the case
  // where the URL carries no usable extension to filter on.
  const declared = anchor.hasAttribute("download");
  if (!declared && !qbShouldCapture(settings, url, "")) return null;
  if (declared && !settings.enabled) return null;
  if (declared && qbIsExcluded(settings, url)) return null;

  return { url, fileName: anchor.getAttribute("download") || "" };
}

document.addEventListener(
  "click",
  (event) => {
    if (!settings || !settings.enabled || !isPlainClick(event)) return;

    const link = downloadLinkFrom(event.target);
    if (!link) return;

    event.preventDefault();
    event.stopPropagation();

    chrome.runtime.sendMessage(
      {
        type: "qb-capture",
        url: link.url,
        fileName: link.fileName,
        referrer: location.href
      },
      (response) => {
        // chrome.runtime.lastError covers the worker being gone entirely; a
        // false ok covers it answering that the bridge refused. Both mean the
        // click has to happen after all.
        if (chrome.runtime.lastError || !response?.ok) {
          window.location.href = link.url;
        }
      }
    );
  },
  true // capture phase: ahead of the page's own click handlers
);
