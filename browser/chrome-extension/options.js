// Options page.
//
// Nothing here requires the user to remember to press Save. Pasting a token and
// pressing the button right next to it — Test connection — is the obvious
// gesture, and an earlier build answered it with "no token set" because the
// Save button was at the far end of the page. Test now saves first, and the
// connection fields also persist when you tab out of them, so the token cannot
// be sitting in a box the service worker never sees.

const $ = (id) => document.getElementById(id);

function setStatus(element, text, kind) {
  element.textContent = text;
  element.className = kind ? `status status--${kind}` : "status";
}

async function load() {
  const settings = await qbGetSettings();

  $("token").value = settings.token;
  $("port").value = settings.port;
  $("enabled").checked = settings.enabled;
  $("fileTypes").value = settings.fileTypes;
  $("excluded").value = settings.excluded;
  $(settings.capture === "all" ? "captureAll" : "captureTypes").checked = true;
}

/** Writes the form to storage. Returns false, having said why, if it couldn't. */
async function save({ announce = true } = {}) {
  const port = Number.parseInt($("port").value, 10);

  try {
    await qbSaveSettings({
      token: $("token").value.trim(),
      // A port outside the range would leave the extension calling an address
      // nothing can listen on, with no error to show for it.
      port: Number.isInteger(port) && port >= 1024 && port <= 65535 ? port : QB_DEFAULTS.port,
      enabled: $("enabled").checked,
      capture: $("captureAll").checked ? "all" : "types",
      fileTypes: $("fileTypes").value.trim(),
      excluded: $("excluded").value.trim()
    });
  } catch (error) {
    // Previously this threw into nothing: no "Saved.", no error, and a settings
    // page that looked like it had taken the change.
    setStatus($("saved"), `Could not save: ${error.message || error}`, "bad");
    return false;
  }

  await load(); // Reflect any value that was clamped on the way in.

  if (announce) {
    setStatus($("saved"), "Saved.", "ok");
    setTimeout(() => setStatus($("saved"), ""), 2500);
  }
  return true;
}

async function test() {
  const status = $("status");
  setStatus(status, "Checking…");

  // Saved first, always. The worker reads storage, not this form, so testing
  // what is on screen means writing it down first — and doing that silently is
  // what makes the button behave the way it looks like it should.
  if (!(await save({ announce: false }))) {
    setStatus(status, "Could not save the settings, so there was nothing to test.", "bad");
    return;
  }

  // Routed through the service worker rather than fetched here: the worker is
  // the only context that keeps the bridge's error handling in one place, and
  // its answer is the same one a real capture would get.
  chrome.runtime.sendMessage({ type: "qb-ping" }, (response) => {
    if (chrome.runtime.lastError) {
      setStatus(status, chrome.runtime.lastError.message, "bad");
      return;
    }

    if (response?.ok) {
      setStatus(status, `Connected to QuickByte ${response.info?.version ?? ""}`.trim(), "ok");
      return;
    }

    setStatus(status, describe(response?.error), "bad");
  });
}

/** Turns the worker's terse errors into something a user can act on. */
function describe(error) {
  const message = String(error || "unreachable");

  if (message.includes("not paired")) return "The token box is empty — copy the token from QuickByte's Browser options.";
  if (message.includes("token rejected")) return "QuickByte rejected the token. Copy it again from its Browser options.";
  if (message.includes("Failed to fetch") || message.includes("abort")) {
    return "No answer on that port. Is QuickByte running with browser integration switched on?";
  }
  return message;
}

document.addEventListener("DOMContentLoaded", () => {
  load();
  $("save").addEventListener("click", () => save());
  $("test").addEventListener("click", test);

  // The two fields the extension is useless without persist as soon as you
  // leave them, so closing the tab can't lose a pairing.
  for (const id of ["token", "port"]) {
    $(id).addEventListener("change", () => save({ announce: false }));
  }
});
