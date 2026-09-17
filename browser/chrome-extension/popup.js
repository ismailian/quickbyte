// Toolbar popup: whether the bridge is answering, one switch to stop capturing
// without opening the options page, and the videos the page's players have loaded.

const $ = (id) => document.getElementById(id);

document.addEventListener("DOMContentLoaded", async () => {
  const settings = await qbGetSettings();
  $("enabled").checked = settings.enabled;

  $("enabled").addEventListener("change", (event) => {
    qbSaveSettings({ enabled: event.target.checked });
  });

  $("options").addEventListener("click", () => qbApi.runtime.openOptionsPage());

  showStreams();

  const status = $("status");

  // Firefox only: the sites have to be granted before anything else can work,
  // so that is the one thing worth saying.
  if (!(await qbHasSiteAccess())) {
    status.textContent = "Needs access to websites — open Options to allow it.";
    status.className = "status status--bad";
    return;
  }

  let response = null;
  try {
    response = await qbApi.runtime.sendMessage({ type: "qb-ping" });
  } catch {
    // The background script is not answering; reported as not connected below.
  }

  if (response?.ok) {
    status.textContent = `Connected · QuickByte ${response.info?.version ?? ""}`.trim();
    status.className = "status status--ok";
    return;
  }

  // One line, and it names the two things that are actually ever wrong.
  status.textContent = "Not connected — check QuickByte is running and paired.";
  status.className = "status status--bad";
});

/**
 * Lists the playlists this tab's players have loaded, each with a button that
 * hands it to QuickByte — where the Add window reads it and offers the qualities.
 * Built with textContent throughout: a playlist address is the page's to choose.
 */
async function showStreams() {
  let tab;
  try {
    [tab] = await qbApi.tabs.query({ active: true, currentWindow: true });
  } catch {
    return;
  }
  if (!tab?.id) return;

  let response = null;
  try {
    response = await qbApi.runtime.sendMessage({ type: "qb-list-streams", tabId: tab.id, page: tab.url });
  } catch {
    return;
  }

  const urls = response?.urls ?? [];
  if (urls.length === 0) return;

  const list = $("stream-list");
  for (const url of urls) {
    const item = document.createElement("li");
    item.className = "streams__item";

    const label = document.createElement("span");
    label.className = "streams__label";
    label.textContent = qbStreamLabel(url);
    label.title = url;

    const send = document.createElement("button");
    send.type = "button";
    send.className = "primary streams__send";
    send.textContent = "Download";
    send.addEventListener("click", async () => {
      send.disabled = true;
      let result = null;
      try {
        result = await qbApi.runtime.sendMessage({ type: "qb-send-stream", url, title: tab.title, page: tab.url });
      } catch {
        // Reported below as not sent.
      }
      send.textContent = result?.ok ? "Sent" : "Failed";
      if (result?.ok) setTimeout(() => window.close(), 600);
      else send.disabled = false;
    });

    item.append(label, send);
    list.append(item);
  }

  $("streams").hidden = false;
}
