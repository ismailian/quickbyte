// Toolbar popup: whether the bridge is answering, and one switch to stop
// capturing without opening the options page.

const $ = (id) => document.getElementById(id);

document.addEventListener("DOMContentLoaded", async () => {
  const settings = await qbGetSettings();
  $("enabled").checked = settings.enabled;

  $("enabled").addEventListener("change", (event) => {
    qbSaveSettings({ enabled: event.target.checked });
  });

  $("options").addEventListener("click", () => chrome.runtime.openOptionsPage());

  chrome.runtime.sendMessage({ type: "qb-ping" }, (response) => {
    const status = $("status");

    if (!chrome.runtime.lastError && response?.ok) {
      status.textContent = `Connected · QuickByte ${response.info?.version ?? ""}`.trim();
      status.className = "status status--ok";
      return;
    }

    // One line, and it names the two things that are actually ever wrong.
    status.textContent = "Not connected — check QuickByte is running and paired.";
    status.className = "status status--bad";
  });
});
