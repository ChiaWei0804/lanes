"use strict";
// Lanes: runs Bilibili-thread-ripper's page code (vendor/btr, unmodified) inside the official Bilibili
// client without touching the client's files. The client is started with a loopback CDP port; the bundle
// is injected into its player page; this process owns the settings. The window is Lanes.exe (app/).
const fs = require("fs"), path = require("path"), http = require("http"), os = require("os");
const { spawn, execFile, execFileSync } = require("child_process");

// The client's process name (four CJK characters), built from code points so the source stays ASCII.
const CLIENT_NAME = String.fromCharCode(0x54d4, 0x54e9, 0x54d4, 0x54e9);
// Lanes.exe finds the install from the uninstall record; this is the default location.
const CLIENT_EXE = process.env.LANES_CLIENT_EXE || `C:\\Program Files\\bilibili\\${CLIENT_NAME}.exe`;
const CDP_PORT = 39229, UI_PORT = 39230;
const SETTINGS_FILE = path.join(__dirname, "settings.json");
// Notepad and PowerShell 5 may save these files with a byte order mark, which JSON.parse rejects, and PowerShell 5's
// ">" writes UTF-16.
const readJsonFile = file => {
  const bytes = fs.readFileSync(file);
  return JSON.parse(bytes.toString(bytes[0] === 0xff && bytes[1] === 0xfe ? "utf16le" : "utf8").replace(/^\uFEFF/, ""));
};
const { version: VERSION, repository: REPOSITORY } = readJsonFile(path.join(__dirname, "version.json"));
const THREADS = ["auto", 8, 16, 32, 64], MODES = ["auto", "overseas", "mainland"], LANGUAGES = ["en", "zh-Hant", "zh-Hans"];
const LEASE_MS = 6000, POLL_MS = 500;
// The running client holds this open; its mtime is the client's start time.
const LOCKFILE = path.join(process.env.APPDATA, "bilibili", "lockfile");
// A client opened while Lanes runs is restarted with the port while it is this fresh (nothing plays yet).
// Automatic restarts are spaced out, and stop after two in a row that did not end in a connection.
const TAKEOVER_AGE_MS = 15000, TAKEOVER_GAP_MS = 10000, TAKEOVER_TRIES = 2;
// Lanes.exe writes these stderr lines to lanes.log, which it clears on every start.
const log = message => console.error(message);

// BTR's page files in its own build order (tools/build.cjs), without its in-client settings UI and updater.
const FILES = ["range-core.js", "cdn-resolver.js", "idm-downloader.js", "runtime-notices.js", "notification-view.js", "settings.js", "transport.js", "client.js"];
const bundle = FILES.map(file => fs.readFileSync(path.join(__dirname, "vendor", "btr", file), "utf8")).join("\n;\n");
// Per-thread speed. BTR takes fetch once, when its bundle loads, so a wrapper installed first sees every piece
// request its downloader makes. Each piece in flight holds a numbered slot (a "thread"), and its bytes are counted
// as the downloader reads them. Only BTR's own piece requests count: an explicit Range plus credentials "omit" and
// cache "no-store". Metering never changes what the downloader receives; if it fails, the plain response goes through.
function installMeter(root) {
  const native = root.fetch.bind(root), slots = [];
  const STALE_MS = 20000; // a slot whose body is never read to the end or cancelled is reused after this
  root.fetch = function (input, init) {
    let slot = null, owner = null;
    try {
      const range = init && init.headers && (init.headers.Range || init.headers.range);
      if (range && init.credentials === "omit" && init.cache === "no-store") {
        const now = Date.now();
        slot = slots.find(item => !item.busy || now - item.at > STALE_MS);
        if (!slot) { slot = { bytes: 0 }; slots.push(slot); }
        // A stale slot may still have its old stream alive: only the current owner counts or frees it.
        owner = {}; slot.owner = owner;
        slot.busy = true; slot.at = now; slot.host = new URL(String(input)).hostname;
      }
    } catch (_) { slot = null; }
    const pending = native(input, init);
    if (!slot) return pending;
    const release = () => { if (slot.owner === owner) slot.busy = false; };
    return pending.then(response => {
      // The downloader drops a non-206 answer without reading it, so the slot is free right away.
      if (response.status !== 206 || !response.body) { release(); return response; }
      try {
        const reader = response.body.getReader();
        const body = new ReadableStream({
          async pull(controller) {
            try {
              const { done, value } = await reader.read();
              if (done) { release(); controller.close(); return; }
              if (slot.owner === owner) { slot.bytes += value.byteLength; slot.at = Date.now(); }
              controller.enqueue(value);
            } catch (error) { release(); controller.error(error); }
          },
          cancel(reason) { release(); return reader.cancel(reason); }
        });
        return new Response(body, { status: response.status, statusText: response.statusText, headers: response.headers });
      } catch (_) { release(); return response; }
    }, error => { release(); throw error; });
  };
  return slots;
}
// Only the player page, only its top frame, and never on top of an installed BTR desktop.
// When this program stops renewing the lease (closed or killed), new requests go native; running ones finish.
const PAGE_SCRIPT = `if (window === top && location.origin === "https://bilipc.bilibili.com" && location.pathname === "/player.html" && !globalThis.__BTR_DESKTOP__) {
globalThis.__BTR_DESKTOP_RELEASE__ = { version: "0.9.4.2-d1+lanes-${VERSION}", adapterRevision: 1 };
globalThis.__BTR_LOCAL__ = { lease: Date.now(), slots: (${installMeter})(globalThis) };
${bundle}
setInterval(() => { const api = globalThis.__BTR_DESKTOP__; if (api && Date.now() - globalThis.__BTR_LOCAL__.lease > ${LEASE_MS} && api.getSettings().enabled) api.setSettings({ enabled: false }); }, 1000);
}`;
// Renews the lease and reads BTR's own status in one round trip. null: no BTR here; foreign: BTR desktop.
const POLL_SCRIPT = `(() => {
  const local = globalThis.__BTR_LOCAL__, api = globalThis.__BTR_DESKTOP__;
  if (!api) return null;
  if (!local) return { foreign: true };
  local.lease = Date.now();
  const status = api.getStatus?.(), t = status?.transport, s = api.getSettings(), media = status?.playback;
  const hostsOk = s.mode !== "custom" || s.customHosts.join() === (globalThis.__BILI_CDN_RESOLVER_FACTORY__?.GLOBAL_HOSTS || []).join();
  return t ? { playing: !!media && !media.paused, settings: { enabled: s.enabled, mode: s.mode, autoConcurrency: s.autoConcurrency, concurrency: s.concurrency, hostsOk }, bytes: t.networkBytes, requests: t.acceleratedRequests, fallbacks: t.fallbackRequests, active: t.activeThreads, threads: t.threads, suspended: t.suspended, slots: (local.slots || []).map(slot => [slot.bytes, slot.host || ""]) } : null;
})()`;

// ---- settings -------------------------------------------------------------------------------------
const DEFAULTS = { enabled: true, threads: "auto", mode: "auto", closeToTray: false, restartRunningClient: true, language: "en" };
// Only known values of the right type are taken, from the window and from a hand-edited settings.json alike.
function merged(base, patch) {
  const next = { ...base };
  for (const key of ["enabled", "closeToTray", "restartRunningClient"]) if (typeof patch[key] === "boolean") next[key] = patch[key];
  if (THREADS.includes(patch.threads)) next.threads = patch.threads;
  if (MODES.includes(patch.mode)) next.mode = patch.mode;
  if (LANGUAGES.includes(patch.language)) next.language = patch.language;
  return next;
}
let settings = { ...DEFAULTS };
try { const saved = readJsonFile(SETTINGS_FILE); if (saved && typeof saved === "object") settings = merged(DEFAULTS, saved); } catch (_) {}
// Region "auto" is BTR's custom mode over all of its nodes, overseas and mainland: BTR measures each node and gives
// the fast ones the video. The node list is BTR's own GLOBAL_HOSTS, filled in by the page (pushScript).
const toBtr = s => ({ enabled: s.enabled, mode: s.mode === "auto" ? "custom" : s.mode, ...(s.threads === "auto" ? { autoConcurrency: true } : { autoConcurrency: false, concurrency: s.threads }) });
// BTR's settings in a page differ from this program's: a new page starts from BTR's stored ones, and a lease may have
// run out while this program was busy.
const needsPush = (page, want) => !page.hostsOk || Object.keys(want).some(key => page[key] !== want[key]);
function applySettings(patch) {
  settings = merged(settings, patch);
  fs.writeFileSync(SETTINGS_FILE, JSON.stringify(settings, null, 2));
  for (const sessionId of pages.keys()) pushSettings(sessionId);
}

// ---- CDP ------------------------------------------------------------------------------------------
let ws = null, nextId = 0, state = "connecting";
const waits = new Map(), pages = new Map(); // sessionId -> { bytes, requests, fallbacks, slots }
function send(method, params = {}, sessionId, timeoutMs = 0) {
  return new Promise((resolve, reject) => {
    if (!ws || ws.readyState !== WebSocket.OPEN) return reject(new Error("CDP closed"));
    const id = ++nextId; waits.set(id, { resolve, reject });
    ws.send(JSON.stringify({ id, method, params, sessionId }));
    // A page that is busy or paused does not answer; give up on it without leaving the wait behind.
    if (timeoutMs) setTimeout(() => { if (waits.delete(id)) reject(new Error(`${method} timeout`)); }, timeoutMs);
  });
}
async function evaluate(sessionId, expression, timeoutMs = 3000) {
  const result = await send("Runtime.evaluate", { expression, returnByValue: true }, sessionId, timeoutMs);
  return result.exceptionDetails ? undefined : result.result.value;
}
const pushScript = s => `(() => {
  if (!globalThis.__BTR_LOCAL__ || !globalThis.__BTR_DESKTOP__) return 0;
  const want = ${JSON.stringify(toBtr(s))};
  if (want.mode === "custom") want.customHosts = globalThis.__BILI_CDN_RESOLVER_FACTORY__.GLOBAL_HOSTS.slice();
  globalThis.__BTR_DESKTOP__.setSettings(want);
  return 0;
})()`;
const pushSettings = sessionId => evaluate(sessionId, pushScript(settings)).catch(() => {});
async function attachPage({ sessionId, waitingForDebugger }) {
  pages.set(sessionId, {});
  // Chrome 108 ignores new-document scripts until the Page domain is enabled.
  send("Page.enable", {}, sessionId).catch(() => {});
  const registered = send("Page.addScriptToEvaluateOnNewDocument", { source: PAGE_SCRIPT }, sessionId).catch(() => {});
  // A new page stays paused, answering nothing, until it is released — awaiting the registration first
  // deadlocks it (black player). A session runs its commands in order, so sending both at once still puts
  // the bundle in place before the page's own scripts; the poll then pushes this program's settings.
  if (waitingForDebugger) return send("Runtime.runIfWaitingForDebugger", {}, sessionId).catch(() => {});
  // A page that was already open when this program started gets the bundle now.
  await registered;
  await evaluate(sessionId, PAGE_SCRIPT + ";0", 10000).catch(() => {});
  await pushSettings(sessionId);
}
async function connect() {
  const { webSocketDebuggerUrl } = await (await fetch(`http://127.0.0.1:${CDP_PORT}/json/version`)).json();
  await new Promise((resolve, reject) => {
    const socket = new WebSocket(webSocketDebuggerUrl);
    socket.onopen = () => { ws = socket; resolve(); };
    socket.onerror = () => reject(new Error("CDP connect failed"));
    socket.onclose = () => {
      if (ws !== socket) return;
      ws = null; pages.clear();
      for (const wait of waits.values()) wait.reject(new Error("CDP closed"));
      waits.clear(); log("Lost the connection to Bilibili"); setState("client-off"); watchClient();
    };
    socket.onmessage = ({ data }) => {
      const m = JSON.parse(data);
      if (m.id) { const wait = waits.get(m.id); waits.delete(m.id); if (wait) m.error ? wait.reject(new Error(m.error.message)) : wait.resolve(m.result); return; }
      if (m.method === "Target.attachedToTarget") attachPage(m.params);
      if (m.method === "Target.detachedFromTarget") pages.delete(m.params.sessionId);
    };
  });
  await send("Target.setAutoAttach", { autoAttach: true, waitForDebuggerOnStart: true, flatten: true, filter: [{ type: "page" }] });
  takeoverTries = 0;
  setState("connected");
}

// ---- status and speed -----------------------------------------------------------------------------
let speed = 0, lastPoll = Date.now(), polling = false;
let stats = { requests: 0, fallbacks: 0, active: 0, threads: 0, suspended: false, playing: false, foreign: false };
// BTR's counters run from the page's load, which may predate this run of Lanes; only growth seen since
// Lanes started counts, and a reloaded page (counter back near zero) just sets a new baseline.
const counted = { requests: 0, fallbacks: 0 };
let threadSpeeds = []; // [{ speed, host }] per slot, bytes/s
function grown(page, key, value) {
  const before = page[key]; page[key] = value;
  return before === undefined || before === null || value < before ? 0 : value - before;
}
async function poll() {
  if (polling) return; polling = true;
  const now = Date.now(), seconds = Math.max(0.001, (now - lastPoll) / 1000); lastPoll = now;
  let bytes = 0; const next = { requests: 0, fallbacks: 0, active: 0, threads: 0, suspended: false, playing: false, foreign: false };
  const slotRates = [], slotHosts = [];
  await Promise.all([...pages].map(async ([sessionId, page]) => {
    const s = await evaluate(sessionId, POLL_SCRIPT).catch(() => undefined);
    if (!s) return;
    if (s.foreign) { next.foreign = true; return; }
    bytes += grown(page, "bytes", s.bytes);
    page.slots ??= [];
    (s.slots || []).forEach(([slotBytes, host], i) => {
      page.slots[i] ??= {};
      slotRates[i] = (slotRates[i] || 0) + grown(page.slots[i], "bytes", slotBytes) / seconds;
      if (host) slotHosts[i] = host;
    });
    counted.requests += grown(page, "requests", s.requests);
    counted.fallbacks += grown(page, "fallbacks", s.fallbacks);
    if (needsPush(s.settings, toBtr(settings))) pushSettings(sessionId);
    next.playing ||= s.playing; next.active += s.active;
    next.threads = Math.max(next.threads, s.threads); next.suspended ||= s.suspended;
  }));
  speed = speed * 0.5 + (bytes / seconds) * 0.5;
  next.requests = counted.requests; next.fallbacks = counted.fallbacks;
  threadSpeeds = slotRates.map((rate, i) => ({ speed: (threadSpeeds[i]?.speed || 0) * 0.5 + rate * 0.5, host: slotHosts[i] || threadSpeeds[i]?.host || "" }));
  if (next.fallbacks > stats.fallbacks) log(`Acceleration failed and a request went back to native download (${next.fallbacks} so far)`);
  if (next.suspended && !stats.suspended) log("A player window kept failing; BTR suspended acceleration there");
  if (next.foreign && !stats.foreign) log("BTR Desktop is installed in the client; Lanes does not inject there");
  stats = next; polling = false;
}

// ---- client process -------------------------------------------------------------------------------
const sleep = ms => new Promise(r => setTimeout(r, ms));
const powershell = command => new Promise(resolve => execFile("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", command], { windowsHide: true }, (_e, out) => resolve(String(out).trim())));
// No process spawn: the lockfile is busy exactly while a client runs.
function clientRunning() {
  try { fs.closeSync(fs.openSync(LOCKFILE, "r+")); return false; } catch (error) { return error.code === "EBUSY"; }
}
// The earliest client process identifies one run of the client (id + start time).
async function clientIdentity() {
  const out = await powershell(`$p = Get-Process -Name '${CLIENT_NAME}' -ErrorAction SilentlyContinue | Sort-Object StartTime | Select-Object -First 1; if ($p) { '{0} {1}' -f $p.Id, ([DateTimeOffset]$p.StartTime).ToUnixTimeMilliseconds() }`);
  const [id, started] = out.split(" ").map(Number);
  return id ? { key: `${id}@${started}`, started } : null;
}
// The lockfile changes whenever the client starts again, so the process query runs once per client run.
let identityCache = { mtime: -1, client: null };
async function currentClient() {
  let mtime = 0;
  try { mtime = fs.statSync(LOCKFILE).mtimeMs; } catch (_) {}
  // A query that found no client is not kept: the next one may (a failed PowerShell start, a client just starting).
  if (mtime !== identityCache.mtime || !identityCache.client) identityCache = { mtime, client: await clientIdentity() };
  return identityCache.client;
}
// Whether to restart a running client (started without the port) so it can be accelerated: one that appeared
// while Lanes runs and is still fresh; or, with "connect to a Bilibili that is already open" on, any other one.
// Each client run is tried once, restarts are spaced out, and two in a row that did not connect stop them.
function shouldTakeOver(client, { initial, attempted, lastTakeoverAt, now, restartRunning, tries }) {
  if (!client || attempted.has(client.key) || now - lastTakeoverAt < TAKEOVER_GAP_MS || tries >= TAKEOVER_TRIES) return false;
  return restartRunning || (client.key !== initial && now - client.started < TAKEOVER_AGE_MS);
}
let startedAt = 0, restarting = false, initialClient = null, lastTakeoverAt = -Infinity, takeoverTries = 0;
const attempted = new Set();
function launchClient() {
  if (restarting) return;
  log("Opening Bilibili with the debugging port");
  // Without this handler a missing client executable would end this process.
  spawn(CLIENT_EXE, [`--remote-debugging-port=${CDP_PORT}`], { detached: true, stdio: "ignore" })
    .on("error", error => { log(`Could not open Bilibili (${CLIENT_EXE}): ${error.message}`); setState("client-off"); }).unref();
  startedAt = Date.now(); setState("starting");
}
// expectedKey: restart only that client run (an automatic takeover), so a different one is never stopped.
async function restartClient(expectedKey) {
  if (restarting) return; restarting = true;
  try {
    if (expectedKey && (await clientIdentity())?.key !== expectedKey) return log("The client to take over is no longer the same run; not restarting it");
    log(expectedKey ? "Restarting Bilibili automatically so it can be accelerated" : "Restarting Bilibili");
    setState("starting"); startedAt = Date.now();
    await powershell(`Stop-Process -Name '${CLIENT_NAME}' -Force -ErrorAction SilentlyContinue`);
    // The client is single-instance: a launch while an old process lingers is swallowed by that process.
    for (let i = 0; i < 40; i++) {
      if (!clientRunning()) { restarting = false; return launchClient(); }
      await sleep(250);
    }
    log("Could not close Bilibili; restart failed"); setState("restart-failed");
  } finally { restarting = false; }
}
let watching = false;
async function watchClient() {
  if (watching) return; watching = true;
  while (!ws && !quitting) {
    try { await connect(); break; } catch (_) {}
    if (state === "starting" && Date.now() - startedAt < 30000) { await sleep(1000); continue; }
    if (clientRunning()) {
      const client = await currentClient(), now = Date.now();
      if (shouldTakeOver(client, { initial: initialClient, attempted, lastTakeoverAt, now, restartRunning: settings.restartRunningClient, tries: takeoverTries })) {
        attempted.add(client.key); lastTakeoverAt = now; takeoverTries++;
        await restartClient(client.key);
        continue;
      }
      if (state !== "restart-failed") setState("needs-restart");
    } else setState("client-off");
    await sleep(1500);
  }
  watching = false;
}

// ---- updates --------------------------------------------------------------------------------------
// Releases on GitHub carry the built folder as Lanes-<version>.zip, and since 1.0.1 also Lanes-<version>-update.zip,
// the same without node.exe (node build.cjs --release). Only files under this repository's releases are taken; the
// small one is optional, so a missing or foreign one just means the full download.
let update = { state: "idle" };
function releaseAssets(release, latest) {
  const ours = name => (release.assets || []).find(item => item.name === name && String(item.browser_download_url).startsWith(`https://github.com/${REPOSITORY}/releases/download/`));
  const pick = item => item && { url: item.browser_download_url, size: Number(item.size) || 0 };
  return { full: pick(ours(`Lanes-${latest}.zip`)), small: pick(ours(`Lanes-${latest}-update.zip`)) };
}
function isNewer(candidate, current) {
  const a = candidate.split(".").map(Number), b = current.split(".").map(Number);
  for (let i = 0; i < 3; i++) if ((a[i] || 0) !== (b[i] || 0)) return (a[i] || 0) > (b[i] || 0);
  return false;
}
// Lanes mostly runs hidden, so it also checks by itself (main). An automatic check that fails only logs and keeps the
// previous result, so an unreachable GitHub does not leave an error in settings; it never replaces a found release.
async function checkUpdate(manual = true) {
  if (["checking", "downloading", "ready"].includes(update.state) || (!manual && update.state === "available")) return;
  const before = update;
  update = { state: "checking" };
  try {
    const res = await fetch(`https://api.github.com/repos/${REPOSITORY}/releases/latest`, { headers: { "User-Agent": `Lanes/${VERSION}`, Accept: "application/vnd.github+json" }, signal: AbortSignal.timeout(15000) });
    if (res.status === 404) update = { state: "latest" }; // no release published yet
    else if (!res.ok) update = { state: "failed", reason: res.status === 403 || res.status === 429 ? "GitHub rate limit" : `HTTP ${res.status}` };
    else {
      const release = await res.json(), latest = String(release.tag_name || "").replace(/^v/i, "");
      const { full, small } = releaseAssets(release, latest);
      if (!/^\d+\.\d+\.\d+$/.test(latest)) update = { state: "failed", reason: `unexpected tag ${release.tag_name}` };
      else if (!isNewer(latest, VERSION)) update = { state: "latest", latest };
      else if (!full) update = { state: "failed", reason: `release ${latest} has no Lanes-${latest}.zip` };
      else update = { state: "available", latest, full, small };
    }
  } catch (error) { update = { state: "failed", reason: error.message }; }
  log(`${manual ? "Update check" : "Automatic update check"}: ${update.state}${update.latest ? ` (${update.latest})` : ""}${update.reason ? ` - ${update.reason}` : ""}`);
  if (!manual && update.state === "failed") update = before;
}
// Waits for Lanes (window and controller) to exit, copies the new files over the folder except the user's
// settings and log, and starts Lanes again. -File passes "12,34" as one string, so the ids are split here.
// A copy that fails part way (a file still locked) would leave old and new files mixed, so the folder is backed
// up first and restored on failure; without a complete backup nothing is copied. update-result.txt tells the next
// start what happened.
const APPLY_SCRIPT = [
  "param([string]$Source, [string]$Target, [string]$Wait, [string]$Work)",
  "foreach ($id in ($Wait -split ',')) { Wait-Process -Id ([int]$id) -Timeout 60 -ErrorAction SilentlyContinue }",
  "Start-Sleep -Milliseconds 500",
  "$result = Join-Path $Target 'update-result.txt'",
  "$backup = Join-Path (Split-Path $Source -Parent) 'backup'",
  "robocopy $Target $backup /E /XD dist /XF settings.json lanes.log /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null",
  "$code = $LASTEXITCODE",
  "if ($code -ge 8) { Set-Content -Path $result -Value \"Update not applied: the current version could not be backed up (robocopy exit $code)\" }",
  "else {",
  "  robocopy $Source $Target /E /XF settings.json lanes.log /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null",
  "  $code = $LASTEXITCODE",
  "  if ($code -lt 8) { Set-Content -Path $result -Value 'Update applied' }",
  "  else {",
  "    robocopy $backup $Target /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null",
  "    $restore = $LASTEXITCODE",
  "    if ($restore -lt 8) { Set-Content -Path $result -Value \"Update failed (robocopy exit $code); the previous version was restored\" }",
  "    else { Set-Content -Path $result -Value \"Update failed (robocopy exit $code) and restoring the previous version failed too (robocopy exit $restore); the previous version is kept in $backup\"; $keep = $true }",
  "  }",
  "}",
  // The download, the unpacked files and the backup (about 130 MB) are not needed any more, unless the backup is
  // the only complete copy left.
  "if (-not $keep -and (Split-Path $Work -Leaf) -like 'lanes-update-*') { Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue }",
  "Start-Process -FilePath (Join-Path $Target 'Lanes.exe')"
].join("\r\n");
// Download and unpack the release, then leave a script that swaps the files in once Lanes has exited
// (Lanes.exe and node.exe are locked while they run), keeps settings.json and lanes.log, and starts Lanes again.
async function installUpdate() {
  if (update.state !== "available") return false;
  const target = update;
  update = { ...target, state: "downloading", progress: 0 };
  let dir = null;
  try {
    dir = fs.mkdtempSync(path.join(os.tmpdir(), "lanes-update-"));
    // The small package fits when it was built with the Node this runs on; otherwise the full one brings node.exe.
    let root = target.small && await fetchPackage(target.small, dir, "update");
    if (root) {
      const node = readJsonFile(path.join(root, "version.json")).node;
      if (node !== process.version) { log(`The update comes with Node ${node || "(unknown)"}, this is ${process.version}; downloading the full package`); root = null; }
    }
    if (!root) root = await fetchPackage(target.full, dir, "full");
    const script = path.join(dir, "apply.ps1");
    fs.writeFileSync(script, APPLY_SCRIPT);
    // Node ends the processes it starts when it exits (a Windows job), and a detached PowerShell has no console and
    // runs nothing. So a short-lived PowerShell starts the script with Start-Process, which puts it outside that job.
    // The arguments travel in an environment variable; paths are double-quoted (a Windows path cannot contain one).
    const args = `-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "${script}" -Source "${root}" -Target "${__dirname}" -Wait ${process.ppid},${process.pid} -Work "${dir}"`;
    const started = await new Promise(resolve => spawn("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "Start-Process -FilePath powershell.exe -WindowStyle Hidden -ArgumentList $env:LANES_APPLY_ARGS"],
      { stdio: "ignore", windowsHide: true, env: { ...process.env, LANES_APPLY_ARGS: args } }).on("exit", resolve).on("error", () => resolve(-1)));
    if (started !== 0) throw new Error(`could not start the update script (PowerShell exit ${started})`);
    update = { ...target, state: "ready" };
    log(`Update ${target.latest} downloaded; it is applied after Lanes exits`);
    return true;
  } catch (error) {
    update = { ...target, state: "install-failed", reason: error.message };
    log(`Update failed: ${error.message}`);
    try { if (dir) fs.rmSync(dir, { recursive: true, force: true }); } catch (_) {}
    return false;
  }
}
// Downloads one release file into dir and unpacks it; returns the folder that holds Lanes.exe.
async function fetchPackage(asset, dir, name) {
  const zip = path.join(dir, `${name}.zip`), files = path.join(dir, name);
  log(`Downloading the ${name} package (${(asset.size / 1048576).toFixed(1)} MB)`);
  update = { ...update, progress: 0 };
  await download(asset.url, zip, asset.size, progress => { update = { ...update, progress }; });
  fs.mkdirSync(files);
  execFileSync(path.join(process.env.WINDIR, "System32", "tar.exe"), ["-xf", zip, "-C", files], { windowsHide: true });
  const root = fs.existsSync(path.join(files, "Lanes.exe")) ? files : path.join(files, "Lanes");
  for (const file of ["Lanes.exe", "btr-local.cjs", "version.json"]) if (!fs.existsSync(path.join(root, file))) throw new Error(`the ${name} package has no ${file}`);
  return root;
}
// Streams a file to disk, reporting whole percents. A slow download goes on as long as data keeps coming; it stops
// after stallMs without any. A byte count other than the expected size (GitHub's asset size, else Content-Length)
// fails, so a cut transfer is never unpacked.
async function download(url, file, size, onProgress, stallMs = 60000) {
  const controller = new AbortController();
  let timer;
  const alive = () => { clearTimeout(timer); timer = setTimeout(() => controller.abort(new Error(`no data for ${stallMs / 1000} s`)), stallMs); };
  // Plain synchronous writes: a write stream reports its errors later, as events, when the folder may be gone.
  const fd = fs.openSync(file, "w");
  alive();
  try {
    const res = await fetch(url, { headers: { "User-Agent": `Lanes/${VERSION}` }, signal: controller.signal });
    if (!res.ok) throw new Error(`download failed: HTTP ${res.status}`);
    const expected = size || Number(res.headers.get("content-length")) || 0;
    let received = 0;
    for await (const chunk of res.body) {
      alive();
      fs.writeSync(fd, chunk);
      received += chunk.length;
      if (expected) onProgress(Math.min(100, Math.floor(received / expected * 100)));
    }
    if (expected && received !== expected) throw new Error(`download incomplete: ${received} of ${expected} bytes`);
  } finally {
    clearTimeout(timer);
    fs.closeSync(fd);
  }
}

// ---- window API ---------------------------------------------------------------------------------
// Only Lanes.exe talks to this: it starts this process with a per-run token and holds its stdin.
// A request with an Origin header comes from some browser page, which must never drive this.
const token = process.env.BTR_TOKEN;
const STATES = { connected: "connected to Bilibili", "client-off": "Bilibili is not open", "needs-restart": "Bilibili runs without the debugging port", starting: "starting Bilibili", "restart-failed": "restarting Bilibili failed" };
function setState(next) { if (next !== state) { state = next; log(`State: ${STATES[next] || next}`); } }
function readJson(req) { return new Promise(resolve => { let body = ""; req.on("data", c => { body += c; if (body.length > 4096) req.destroy(); }); req.on("end", () => { try { const value = JSON.parse(body); resolve(value && typeof value === "object" ? value : {}); } catch (_) { resolve({}); } }); }); }
const server = http.createServer(async (req, res) => {
  const url = new URL(req.url, "http://x");
  if (req.headers.host !== `127.0.0.1:${UI_PORT}` || req.headers.origin !== undefined || req.headers["x-btr-token"] !== token) { res.writeHead(403); return res.end(); }
  if (req.method === "GET" && url.pathname === "/status") {
    res.writeHead(200, { "Content-Type": "application/json; charset=utf-8" });
    return res.end(JSON.stringify({ state, settings, version: VERSION, update: { state: update.state, latest: update.latest || "", progress: update.progress || 0 }, speed: Math.round(speed), ...stats,
      threadSpeeds: threadSpeeds.map(t => ({ speed: Math.round(t.speed), host: t.host })) }));
  }
  if (req.method === "POST" && url.pathname === "/settings") { applySettings(await readJson(req)); res.writeHead(204); return res.end(); }
  if (req.method === "POST" && url.pathname === "/client") {
    const { action } = await readJson(req);
    if (action === "launch") launchClient(); else if (action === "restart") restartClient();
    res.writeHead(204); return res.end();
  }
  if (req.method === "POST" && url.pathname === "/update/check") { checkUpdate(); res.writeHead(204); return res.end(); }
  // The download runs on; the window watches update.state and exits once it reads "ready".
  if (req.method === "POST" && url.pathname === "/update/install") { installUpdate(); res.writeHead(204); return res.end(); }
  res.writeHead(404); res.end();
});
let quitting = false;
async function shutdown(reason) {
  if (quitting) return; quitting = true;
  log(`Controller exiting (${reason})`);
  // Stop accelerating new requests; downloads already running inside the page finish on their own.
  await Promise.all([...pages.keys()].map(id => evaluate(id, `globalThis.__BTR_LOCAL__ && globalThis.__BTR_DESKTOP__?.setSettings({ enabled: false }), 0`).catch(() => {})));
  ws?.close(); process.exit(0);
}

function main() {
  if (!token) { log("Start this through Lanes.exe (BTR_TOKEN is missing)"); process.exit(2); }
  const login = process.env.LANES_LOGIN === "1";
  log(`Controller ${VERSION} started${login ? " at sign-in" : ""}, Node ${process.version}, client: ${CLIENT_EXE}`);
  const updateResult = path.join(__dirname, "update-result.txt");
  if (fs.existsSync(updateResult)) { log(fs.readFileSync(updateResult, "utf8").trim()); fs.rmSync(updateResult, { force: true }); }
  process.on("uncaughtException", error => { log(`Controller error: ${error.stack || error}`); process.exit(1); });
  process.on("unhandledRejection", error => log(`Controller error: ${error?.stack || error}`));
  process.on("SIGINT", () => shutdown("SIGINT"));
  // The window's end of this pipe closes when the window exits, and also when it crashes.
  process.stdin.on("end", () => shutdown("window closed")); process.stdin.on("close", () => shutdown("window closed")); process.stdin.resume();
  setInterval(() => { if (ws) poll(); }, POLL_MS);
  // Installing restarts Lanes, so it waits for the Update button; this only finds the release.
  setTimeout(() => checkUpdate(false), 30000);
  setInterval(() => checkUpdate(false), 24 * 3600 * 1000);
  server.on("error", error => { log(`Window API error: ${error.message}`); process.exit(1); });
  server.listen(UI_PORT, "127.0.0.1", async () => {
    try { await connect(); return; } catch (_) {}
    // Lanes never opens the client by itself: it waits, and connects to whatever client appears. One that is
    // already running now is restarted only with "connect to a Bilibili that is already open" on.
    if (clientRunning()) initialClient = (await currentClient())?.key ?? null;
    watchClient();
  });
}
if (require.main === module) main(); else module.exports = { shouldTakeOver, installMeter, isNewer, APPLY_SCRIPT, merged, DEFAULTS, toBtr, needsPush, pushScript, POLL_SCRIPT, checkUpdate, updateState: () => update.state, releaseAssets, download, installUpdate };
