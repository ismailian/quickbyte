// Toolbar popup: whether the bridge is answering, and one switch to stop
// capturing without opening the options page.

const $ = (id) => document.getElementById(id);

document.addEventListener("DOMContentLoaded", async () => {
  const settings = await qbGetSettings();
  $("enabled").checked = settings.enabled;

  $("enabled").addEventListener("change", (event) => {
    qbSaveSettings({ enabled: event.target.checked });
  });

  $("options").addEventListener("click", () => qbApi.runtime.openOptionsPage());

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
