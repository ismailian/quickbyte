// Tests for the rules in chrome-extension/shared.js — which downloads are taken
// over, and which browser API the extension talks to.
//
//   node --test browser/tests
//
// shared.js is plain globals (it is loaded by importScripts, a background-script
// list and <script> tags alike), so it is run here inside a vm context with a fake
// extension API rather than imported as a module.

import { test } from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const source = readFileSync(fileURLToPath(new URL("../chrome-extension/shared.js", import.meta.url)), "utf8");

function load({ browser, chrome } = {}) {
  const context = vm.createContext({ URL, globalThis: undefined });
  context.globalThis = context;
  if (browser) context.browser = browser;
  if (chrome) context.chrome = chrome;
  vm.runInContext(source, context);
  return context;
}

const run = (context, expression) => vm.runInContext(expression, context);

function fakeStorage(initial = {}) {
  const data = { ...initial };
  return {
    data,
    async get(defaults) {
      return { ...defaults, ...Object.fromEntries(Object.keys(defaults).filter((k) => k in data).map((k) => [k, data[k]])) };
    },
    async set(values) {
      Object.assign(data, values);
    }
  };
}

const settings = (overrides = {}) => ({
  enabled: true, port: 9614, token: "t", capture: "types",
  fileTypes: "zip, iso, exe", excluded: "", ...overrides
});

// ------------------------------------------------------------ which browser --

test("Firefox's browser namespace is used when there is one", () => {
  const browser = { marker: "firefox" };
  const context = load({ browser, chrome: { marker: "chrome-compat" } });
  assert.equal(run(context, "qbApi.marker"), "firefox");
});

test("Chrome and Edge use the chrome namespace", () => {
  const context = load({ chrome: { marker: "chrome" } });
  assert.equal(run(context, "qbApi.marker"), "chrome");
});

test("a browser that cannot say whether it granted site access is taken to have granted it", async () => {
  const context = load({ chrome: { permissions: { contains: async () => { throw new Error("no"); } } } });
  assert.equal(await run(context, "qbHasSiteAccess()"), true);
});

test("site access is whatever the browser reports", async () => {
  const context = load({ browser: { permissions: { contains: async () => false } } });
  assert.equal(await run(context, "qbHasSiteAccess()"), false);
});

// ----------------------------------------------------------- what is taken --

test("a listed file type is taken over, judged by the path and not the query", () => {
  const context = load({ chrome: {} });
  context.s = settings();
  assert.equal(run(context, 'qbShouldCapture(s, "https://example.com/setup.exe?x=1.html", "")'), true);
  assert.equal(run(context, 'qbShouldCapture(s, "https://example.com/page.html?f=setup.exe", "")'), false);
});

test("the browser's own suggested name counts when the URL is opaque", () => {
  const context = load({ chrome: {} });
  context.s = settings();
  assert.equal(run(context, 'qbShouldCapture(s, "https://example.com/files/8fd21ac3", "disk.ISO")'), true);
});

test("nothing is taken while switched off, or from blob: and data: URLs that only exist in the page", () => {
  const context = load({ chrome: {} });
  context.off = settings({ enabled: false });
  context.all = settings({ capture: "all" });
  assert.equal(run(context, 'qbShouldCapture(off, "https://example.com/a.zip", "")'), false);
  assert.equal(run(context, 'qbShouldCapture(all, "blob:https://example.com/123", "a.zip")'), false);
  assert.equal(run(context, 'qbShouldCapture(all, "data:application/zip;base64,AA", "a.zip")'), false);
});

test("capturing everything still stops at an excluded site and its subdomains, but not a lookalike", () => {
  const context = load({ chrome: {} });
  context.s = settings({ capture: "all", excluded: "example.com" });
  assert.equal(run(context, 'qbShouldCapture(s, "https://example.com/a", "")'), false);
  assert.equal(run(context, 'qbShouldCapture(s, "https://cdn.example.com/a", "")'), false);
  assert.equal(run(context, 'qbShouldCapture(s, "https://notexample.com/a", "")'), true);
});

test("the file-type list tolerates dots, commas, semicolons and case", () => {
  const context = load({ chrome: {} });
  context.s = settings({ fileTypes: " .ZIP, rar;iso\n7z " });
  assert.deepEqual([...run(context, "qbFileTypeSet(s)")].sort(), ["7z", "iso", "rar", "zip"]);
});

// ------------------------------------------------------------------- storage --

test("a pairing made by an older build in sync storage is carried into local once", async () => {
  const local = fakeStorage();
  const sync = fakeStorage({ token: "from-sync", port: 9700 });
  const context = load({ chrome: { storage: { local, sync } } });

  const loaded = await run(context, "qbGetSettings()");

  assert.equal(loaded.token, "from-sync");
  assert.equal(local.data.token, "from-sync");
  assert.equal(local.data.port, 9700);
});

test("a pairing already in local storage is never overwritten from sync", async () => {
  const local = fakeStorage({ token: "local" });
  const sync = fakeStorage({ token: "stale" });
  const context = load({ browser: { storage: { local, sync } } });

  const loaded = await run(context, "qbGetSettings()");

  assert.equal(loaded.token, "local");
  assert.equal(local.data.token, "local");
});

// ------------------------------------------------------------------- streams --

test("a video player's playlist or manifest is recognised by its path, whatever the query", () => {
  const context = load({ chrome: {} });
  assert.equal(run(context, 'qbIsStreamManifest("https://cdn.example.com/v/master.m3u8?token=abc")'), true);
  assert.equal(run(context, 'qbIsStreamManifest("https://cdn.example.com/v/manifest.MPD")'), true);
  assert.equal(run(context, 'qbIsStreamManifest("https://cdn.example.com/v/segment-12.ts")'), false);
  assert.equal(run(context, 'qbIsStreamManifest("https://example.com/watch?list=a.m3u8")'), false);
  assert.equal(run(context, 'qbIsStreamManifest("blob:https://example.com/5b1f")'), false);
  assert.equal(run(context, 'qbIsStreamManifest("file:///C:/videos/local.m3u8")'), false);
});

test("a tab remembers each playlist once, newest first, and only so many", () => {
  const context = load({ chrome: {} });
  let list = [];
  context.remember = (url) => { context.list = list; list = run(context, `qbRememberStream(list, ${JSON.stringify(url)})`); };

  context.remember("https://a.example.com/1.m3u8");
  context.remember("https://a.example.com/2.m3u8");
  context.remember("https://a.example.com/1.m3u8");
  assert.deepEqual([...list], ["https://a.example.com/1.m3u8", "https://a.example.com/2.m3u8"]);

  for (let i = 0; i < 50; i++) context.remember(`https://a.example.com/r${i}.m3u8`);
  assert.equal(list.length, run(context, "QB_MAX_STREAMS"));
  assert.equal(list[0], "https://a.example.com/r49.m3u8");
});

test("a stream is named after its page, without the characters Windows refuses", () => {
  const context = load({ chrome: {} });
  assert.equal(
    run(context, 'qbStreamFileName("Keynote: What\'s <new> in 2026? | Example", "https://x.example.com/master.m3u8")'),
    "Keynote What's new in 2026 Example.m3u8");
  assert.equal(run(context, 'qbStreamFileName("Talk...", "https://x.example.com/manifest.mpd")'), "Talk.mpd");
  assert.equal(run(context, 'qbStreamFileName("   ", "https://x.example.com/master.m3u8")'), "");
  assert.equal(run(context, 'qbStreamFileName("A".repeat(300), "https://x.example.com/a.m3u8").length'), 125);
});

test("a playlist is listed by the end of its path and its host", () => {
  const context = load({ chrome: {} });
  assert.equal(
    run(context, 'qbStreamLabel("https://cdn.example.com/show/Launch%20Day/master.m3u8?sig=1")'),
    "Launch Day/master.m3u8 · cdn.example.com");
  assert.equal(run(context, 'qbStreamLabel("https://cdn.example.com/bad%E0%A4%A/x.mpd")'), "bad%E0%A4%A/x.mpd · cdn.example.com");
});
