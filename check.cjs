"use strict";
// Self-checks for Lanes. Run: node check.cjs
const assert = require("assert"), fs = require("fs"), path = require("path"), vm = require("vm");
const { shouldTakeOver, installMeter, isNewer, merged, DEFAULTS, toBtr, needsPush, pushScript, POLL_SCRIPT, checkUpdate, updateState } = require("./btr-local.cjs");

// ---- takeover rule ----
const now = 1000000, base = { initial: null, attempted: new Set(), lastTakeoverAt: -Infinity, now, restartRunning: false, tries: 0 };
const fresh = { key: `1@${now - 3000}`, started: now - 3000 }, old = { key: `2@${now - 600000}`, started: now - 600000 };
assert.equal(shouldTakeOver(fresh, base), true, "a client opened while Lanes runs, 3 s ago");
assert.equal(shouldTakeOver(null, base), false, "no client");
assert.equal(shouldTakeOver(old, base), false, "an older client may be playing");
assert.equal(shouldTakeOver(fresh, { ...base, initial: fresh.key }), false, "the client already running when Lanes started");
assert.equal(shouldTakeOver(old, { ...base, initial: old.key, restartRunning: true }), true, "with the setting on, the running client is restarted");
assert.equal(shouldTakeOver(fresh, { ...base, attempted: new Set([fresh.key]) }), false, "a client run already tried once");
assert.equal(shouldTakeOver(fresh, { ...base, lastTakeoverAt: now - 5000 }), false, "too soon after the last takeover");
assert.equal(shouldTakeOver(old, { ...base, restartRunning: true, tries: 2 }), false, "two takeovers in a row that never connected stop them");
console.log("takeover rule: ok");

// ---- version comparison ----
assert.equal(isNewer("1.0.1", "1.0.0"), true); assert.equal(isNewer("1.10.0", "1.9.9"), true);
assert.equal(isNewer("1.0.0", "1.0.0"), false); assert.equal(isNewer("0.9.9", "1.0.0"), false);
console.log("version comparison: ok");

// ---- CDN region: "auto" is BTR's custom mode over all of BTR's own nodes ----
assert.equal(DEFAULTS.mode, "auto");
assert.equal(merged(DEFAULTS, { mode: "mainland" }).mode, "mainland", "a saved manual choice stays");
assert.equal(merged({ ...DEFAULTS, mode: "overseas" }, { mode: "custom" }).mode, "overseas", "BTR's own custom mode is not a Lanes choice");
// A player page with BTR's real node lists and a stand-in for its settings store.
const page = { URL, location: { href: "https://bilipc.bilibili.com/player.html" } };
vm.createContext(page);
for (const file of ["range-core.js", "cdn-resolver.js"]) vm.runInContext(fs.readFileSync(path.join(__dirname, "vendor", "btr", file), "utf8"), page);
const nodes = page.__BILI_CDN_RESOLVER_FACTORY__;
let stored = { enabled: true, mode: "overseas", customHosts: [], autoConcurrency: true, concurrency: 8 };
page.__BTR_LOCAL__ = { lease: 0, slots: [] };
page.__BTR_DESKTOP__ = { getSettings: () => ({ ...stored, customHosts: stored.customHosts.slice() }), setSettings: patch => { stored = { ...stored, ...patch }; }, getStatus: () => ({ transport: {}, playback: null }) };
const pageSettings = () => vm.runInContext(POLL_SCRIPT, page).settings;
const auto = { ...DEFAULTS, mode: "auto" }, mainland = { ...DEFAULTS, mode: "mainland" };
assert.equal(needsPush(pageSettings(), toBtr(auto)), true, "an overseas page is switched to auto");
vm.runInContext(pushScript(auto), page);
assert.equal(stored.mode, "custom");
assert.deepEqual(stored.customHosts, [...nodes.OVERSEAS_HOSTS, ...nodes.MAINLAND_HOSTS], "auto uses every BTR node, overseas and mainland");
assert.equal(needsPush(pageSettings(), toBtr(auto)), false, "no repeated push once the page matches");
stored.customHosts = ["upos-sz-mirrorali.bilivideo.com"];
assert.equal(needsPush(pageSettings(), toBtr(auto)), true, "a custom list other than BTR's full one is replaced");
vm.runInContext(pushScript(mainland), page);
assert.equal(stored.mode, "mainland"); assert.equal(needsPush(pageSettings(), toBtr(mainland)), false);
console.log("cdn region: ok");

// ---- languages: same keys everywhere, and code without Chinese text ----
const lang = name => JSON.parse(fs.readFileSync(path.join(__dirname, "app", "lang", `${name}.json`), "utf8"));
const keys = Object.keys(lang("en")).sort();
for (const name of ["zh-Hant", "zh-Hans"]) assert.deepEqual(Object.keys(lang(name)).sort(), keys, `${name}.json has the same keys as en.json`);
const used = new Set([...fs.readFileSync(path.join(__dirname, "app", "Lanes.cs"), "utf8").matchAll(/T\("([^"]+)"/g), ...fs.readFileSync(path.join(__dirname, "app", "ui.xaml"), "utf8").matchAll(/DynamicResource T\.([^}]+)\}/g)].map(m => m[1]));
// CJK punctuation, CJK ideographs and full-width forms, by code point so this file stays ASCII.
const isChinese = c => { const n = c.codePointAt(0); return (n >= 0x3000 && n <= 0x303f) || (n >= 0x3400 && n <= 0x9fff) || (n >= 0xff00 && n <= 0xffef); };
for (const key of used) assert.ok(keys.includes(key), `"${key}" is used but missing from en.json`);
for (const file of ["btr-local.cjs", "build.cjs", "check.cjs", "app/Lanes.cs", "app/ui.xaml"]) {
  const bad = fs.readFileSync(path.join(__dirname, file), "utf8").split(/\r?\n/).findIndex(line => [...line].some(isChinese));
  assert.equal(bad, -1, `${file} line ${bad + 1} has Chinese text; it belongs in app/lang`);
}
console.log(`languages: ok (${keys.length} strings, ${used.size} used)`);

// ---- per-thread meter ----
(async () => {
  // A fake page fetch: 206 with the given chunks, or another status, or a network failure.
  const answer = (status, chunks) => new Response(new ReadableStream({ start(c) { chunks.forEach(n => c.enqueue(new Uint8Array(n))); c.close(); } }), { status, headers: { "Content-Range": "bytes 0-9/10" } });
  const root = { fetch: async (url, init) => url.includes("fail") ? Promise.reject(new TypeError("network")) : answer(url.includes("403") ? 403 : 206, [1000, 500]) };
  const slots = installMeter(root);
  const piece = { headers: { Range: "bytes=0-1499" }, credentials: "omit", cache: "no-store" };
  const read = async response => new Uint8Array(await new Response(response.body).arrayBuffer()).byteLength;

  const [a, b] = await Promise.all([root.fetch("https://a.bilivideo.com/x.m4s", piece), root.fetch("https://b.bilivideo.com/x.m4s", piece)]);
  assert.equal(slots.length, 2, "two pieces in flight take two slots");
  assert.deepEqual(slots.map(s => s.busy), [true, true]);
  assert.equal(a.status, 206); assert.equal(a.headers.get("content-range"), "bytes 0-9/10", "status and headers pass through");
  assert.equal(await read(a), 1500, "the downloader gets every byte"); assert.equal(await read(b), 1500);
  assert.deepEqual(slots.map(s => [s.bytes, s.busy, s.host]), [[1500, false, "a.bilivideo.com"], [1500, false, "b.bilivideo.com"]], "bytes counted, slots freed at the end");

  await read(await root.fetch("https://c.bilivideo.com/x.m4s", piece));
  assert.equal(slots.length, 2, "a free slot is reused"); assert.equal(slots[0].bytes, 3000);

  await read(await root.fetch("https://c.bilivideo.com/x.m4s", { headers: { Range: "bytes=0-9" } }));
  await read(await root.fetch("https://api.bilibili.com/x", {}));
  assert.equal(slots[0].bytes + slots[1].bytes, 4500, "requests that are not BTR pieces are not metered");

  const refused = await root.fetch("https://403.bilivideo.com/x.m4s", piece);
  assert.equal(refused.status, 403); assert.equal(slots[0].busy, false, "a non-206 answer frees its slot at once");
  await assert.rejects(root.fetch("https://fail.bilivideo.com/x.m4s", piece)); assert.equal(slots[0].busy, false, "a failed request frees its slot");
  const cancelled = await root.fetch("https://d.bilivideo.com/x.m4s", piece);
  await cancelled.body.getReader().cancel("switched video"); assert.equal(slots[0].busy, false, "a cancelled read frees its slot");

  // A stream left unread past the stale limit loses its slot; if it resumes later it must not count there.
  const realNow = Date.now;
  const stale = await root.fetch("https://e.bilivideo.com/x.m4s", piece);
  const staleSlot = slots.find(s => s.busy);
  Date.now = () => realNow() + 21000;
  const fresher = await root.fetch("https://f.bilivideo.com/x.m4s", piece);
  assert.equal(slots.filter(s => s.busy).length, 1, "the stale slot went to the new request");
  await new Promise(r => setTimeout(r, 10)); // a stream fills its first chunk on its own, a moment later
  const before = staleSlot.bytes;
  await read(stale);
  assert.equal(staleSlot.bytes, before, "the old stream's late bytes do not count for the new owner");
  assert.equal(staleSlot.busy, true, "the old stream's end does not free the new owner's slot");
  await read(fresher); Date.now = realNow;
  assert.equal(staleSlot.busy, false);
  console.log("thread meter: ok");

  // ---- automatic update checks only find a release; one that fails keeps the last result ----
  const quiet = console.error; console.error = () => {};
  let calls = 0;
  const github = reply => { global.fetch = async () => { calls++; return reply(); }; };
  const release = v => ({ ok: true, status: 200, json: async () => ({ tag_name: `v${v}`, assets: [{ name: `Lanes-${v}.zip`, browser_download_url: `https://github.com/${require("./version.json").repository}/releases/download/v${v}/Lanes-${v}.zip` }] }) });
  github(() => ({ ok: false, status: 404 })); await checkUpdate(false);
  assert.equal(updateState(), "latest", "no release published yet");
  github(() => { throw new TypeError("fetch failed"); }); await checkUpdate(false);
  assert.equal(updateState(), "latest", "an automatic check that fails keeps the last result");
  await checkUpdate(true);
  assert.equal(updateState(), "failed", "a manual check shows its failure");
  github(() => release("99.0.0")); await checkUpdate(false);
  assert.equal(updateState(), "available");
  calls = 0; await checkUpdate(false);
  assert.equal(calls, 0, "a found release is not checked again automatically"); assert.equal(updateState(), "available");
  console.error = quiet;
  console.log("update checks: ok");
})().catch(error => { console.error(error); process.exit(1); });
