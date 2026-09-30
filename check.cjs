"use strict";
// Self-checks for Lanes. Run after node build.cjs: node check.cjs
// Lanes.exe checks its own C# (--self-check); this file checks the JavaScript that runs inside the player page, on the
// exact scripts Lanes.exe injects (--dump-page-scripts), and the language files.
const assert = require("assert"), fs = require("fs"), path = require("path"), vm = require("vm"), os = require("os");
const { execFileSync } = require("child_process");
const exe = path.join(__dirname, "Lanes.exe");

process.stdout.write(execFileSync(exe, ["--self-check"], { encoding: "utf8", stdio: ["ignore", "pipe", "inherit"] }));

const scripts = fs.mkdtempSync(path.join(os.tmpdir(), "lanes-check-"));
execFileSync(exe, ["--dump-page-scripts", scripts]);
const dumped = Object.fromEntries(fs.readdirSync(scripts).map(name => [name, fs.readFileSync(path.join(scripts, name), "utf8")]));
const script = name => dumped[name];
const POLL_SCRIPT = script("poll.js"), INJECT = script("inject.js");
const installMeter = vm.runInThisContext(`(${script("meter.js")})`); // with this realm's fetch classes, as in a page
const vendor = file => fs.readFileSync(path.join(__dirname, "vendor", "btr", file), "utf8");
fs.rmSync(scripts, { recursive: true, force: true });

// ---- CDN region: "auto" is BTR's custom mode over all of BTR's own nodes ----
// A player page with BTR's real node lists and a stand-in for its settings store.
const page = { URL, location: { href: "https://bilipc.bilibili.com/player.html" } };
vm.createContext(page);
for (const file of ["range-core.js", "cdn-resolver.js"]) vm.runInContext(vendor(file), page);
const nodes = page.__BILI_CDN_RESOLVER_FACTORY__;
let stored = { enabled: true, mode: "overseas", customHosts: [], autoConcurrency: true, concurrency: 8 };
page.__BTR_LOCAL__ = { lease: 0, slots: [] };
page.__BTR_DESKTOP__ = { getSettings: () => ({ ...stored, customHosts: stored.customHosts.slice() }), setSettings: patch => { stored = { ...stored, ...patch }; }, getStatus: () => ({ transport: {}, playback: null }) };
const pageSettings = () => vm.runInContext(POLL_SCRIPT, page).settings;
assert.equal(pageSettings().hostsOk, true, "overseas needs no host list");
vm.runInContext(script("push-auto.js"), page);
assert.equal(stored.mode, "custom");
assert.deepEqual(stored.customHosts, [...nodes.OVERSEAS_HOSTS, ...nodes.MAINLAND_HOSTS], "auto uses every BTR node, overseas and mainland");
assert.equal(pageSettings().hostsOk, true, "the page reports BTR's full list");
stored.customHosts = ["upos-sz-mirrorali.bilivideo.com"];
assert.equal(pageSettings().hostsOk, false, "a custom list other than BTR's full one is reported");
vm.runInContext(script("push-mainland.js"), page);
assert.deepEqual([stored.mode, stored.autoConcurrency, stored.concurrency], ["mainland", false, 16]);
assert.ok(page.__BTR_LOCAL__.lease > 0, "the poll renews the lease");
assert.deepEqual(vm.runInNewContext(POLL_SCRIPT, { __BTR_DESKTOP__: {} }), { foreign: true }, "BTR installed in the client is left alone");
assert.equal(vm.runInNewContext(POLL_SCRIPT, {}), null, "a page without BTR");
console.log("cdn region: ok");

// ---- the injected bundle: only in the main player page, once; turns BTR off when the lease runs out ----
// BTR's files go in unmodified and in its build order; the rest of the script runs here without them (they need a
// real browser).
const bundle = ["range-core.js", "cdn-resolver.js", "idm-downloader.js", "runtime-notices.js", "notification-view.js", "settings.js", "transport.js", "client.js"].map(vendor).join("\n;\n");
assert.ok(INJECT.includes(bundle), "the injected script carries BTR's files unmodified, in build order");
const wrapper = INJECT.replace(bundle, "");
const player = (href, extra) => {
  const url = new URL(href), timers = [];
  const context = { URL, location: { href, origin: url.origin, pathname: url.pathname }, setInterval: (f, ms) => timers.push(f), ...extra };
  context.window = context.top = context;
  vm.createContext(context);
  vm.runInContext(wrapper, context);
  return { context, timers };
};
assert.equal(player("https://www.bilibili.com/player.html").context.__BTR_LOCAL__, undefined, "another origin gets nothing");
assert.equal(player("https://bilipc.bilibili.com/index.html").context.__BTR_LOCAL__, undefined, "another page gets nothing");
const framed = { URL, location: { href: "https://bilipc.bilibili.com/player.html", origin: "https://bilipc.bilibili.com", pathname: "/player.html" }, top: {} };
framed.window = framed; vm.createContext(framed); vm.runInContext(wrapper, framed);
assert.equal(framed.__BTR_LOCAL__, undefined, "a frame gets nothing");
const installed = { getSettings: () => ({}), setSettings() {} };
assert.equal(player("https://bilipc.bilibili.com/player.html", { __BTR_DESKTOP__: installed }).context.__BTR_LOCAL__, undefined, "BTR installed in the client is left alone");
let off = 0;
const leased = player("https://bilipc.bilibili.com/player.html", { fetch: async () => new Response("") });
const { context: ctx, timers } = leased;
assert.ok(ctx.__BTR_LOCAL__ && Array.isArray(ctx.__BTR_LOCAL__.slots), "the main player page gets the meter");
const btrVersion = vendor("settings.js").match(/__BTR_DESKTOP_RELEASE__\?\.version \|\| "([^"]+)"/)[1];
assert.equal(ctx.__BTR_DESKTOP_RELEASE__.version, `${btrVersion}+lanes-${require("./version.json").version}`, "the injected release names the vendored BTR version");
ctx.__BTR_DESKTOP__ = { getSettings: () => ({ enabled: true }), setSettings: patch => { if (patch.enabled === false) off++; } };
timers.forEach(f => f());
assert.equal(off, 0, "a fresh lease keeps BTR on");
ctx.__BTR_LOCAL__.lease = Date.now() - 7000;
timers.forEach(f => f());
assert.equal(off, 1, "an expired lease turns BTR off");
// Lanes' settings (the defaults in the dumped script) reach BTR right after its bundle, once per window.
const session = new Map(), applied = [];
const stub = "globalThis.__BTR_DESKTOP__ = { getSettings: () => ({ enabled: true }), setSettings: s => globalThis.applied.push(s) }; globalThis.__BILI_CDN_RESOLVER_FACTORY__ = { GLOBAL_HOSTS: ['a.bilivideo.com', 'b.bilivideo.com'] };";
const openWindow = () => {
  const c = { URL, applied, setInterval() {}, fetch: async () => new Response(""), location: { href: "https://bilipc.bilibili.com/player.html", origin: "https://bilipc.bilibili.com", pathname: "/player.html" },
    sessionStorage: { getItem: k => session.has(k) ? session.get(k) : null, setItem: (k, v) => session.set(k, String(v)) } };
  c.window = c.top = c;
  vm.createContext(c);
  vm.runInContext(INJECT.replace(bundle, stub), c);
};
openWindow();
assert.deepEqual(JSON.parse(JSON.stringify(applied)), [{ enabled: true, mode: "custom", autoConcurrency: true, customHosts: ["a.bilivideo.com", "b.bilivideo.com"] }], "a new window starts with Lanes' settings");
openWindow();
assert.equal(applied.length, 1, "a reload of the same window keeps BTR's stored settings");
console.log("injected bundle: ok");

// ---- languages: same keys everywhere, and code without Chinese text ----
const lang = name => JSON.parse(fs.readFileSync(path.join(__dirname, "app", "lang", `${name}.json`), "utf8"));
const keys = Object.keys(lang("en")).sort();
for (const name of ["zh-Hant", "zh-Hans"]) assert.deepEqual(Object.keys(lang(name)).sort(), keys, `${name}.json has the same keys as en.json`);
const code = ["build.cjs", "check.cjs", "app/ui.xaml", ...fs.readdirSync(path.join(__dirname, "app")).filter(f => f.endsWith(".cs")).map(f => `app/${f}`), ...fs.readdirSync(path.join(__dirname, "app", "page")).map(f => `app/page/${f}`)];
const read = file => fs.readFileSync(path.join(__dirname, file), "utf8");
const used = new Set([...read("app/Lanes.cs").matchAll(/T\("([^"]+)"/g), ...read("app/ui.xaml").matchAll(/DynamicResource T\.([^}]+)\}/g)].map(m => m[1]));
// Lanes.cs also builds keys at run time ("state." + state, notices); those are listed in its tables.
for (const m of read("app/Lanes.cs").matchAll(/"((?:notice|status|update|state)\.[A-Za-z.]+)"/g)) used.add(m[1]);
// CJK punctuation, CJK ideographs and full-width forms, by code point so this file stays ASCII.
const isChinese = c => { const n = c.codePointAt(0); return (n >= 0x3000 && n <= 0x303f) || (n >= 0x3400 && n <= 0x9fff) || (n >= 0xff00 && n <= 0xffef); };
for (const key of used) assert.ok(keys.includes(key), `"${key}" is used but missing from en.json`);
for (const file of code) {
  const text = read(file);
  const bad = text.split(/\r?\n/).findIndex(line => [...line].some(isChinese));
  assert.equal(bad, -1, `${file} line ${bad + 1} has Chinese text; it belongs in app/lang`);
  assert.ok(!text.includes(String.fromCharCode(0xfeff)), `${file} has a byte order mark character`);
}
console.log(`languages: ok (${keys.length} strings, ${used.size} used)`);

// ---- per-thread meter ----
(async () => {
  // A fake page fetch: 206 with the given chunks, or another status, or a network failure.
  const answer = (status, chunks) => new Response(new ReadableStream({ start(c) { chunks.forEach(n => c.enqueue(new Uint8Array(n))); c.close(); } }), { status, headers: { "Content-Range": "bytes 0-9/10" } });
  const root = { fetch: async (url, init) => url.includes("fail") ? Promise.reject(new TypeError("network")) : answer(url.includes("403") ? 403 : 206, [1000, 500]) };
  const slots = installMeter(root);
  const piece = { headers: { Range: "bytes=0-1499" }, credentials: "omit", cache: "no-store" };
  const readAll = async response => new Uint8Array(await new Response(response.body).arrayBuffer()).byteLength;

  const [a, b] = await Promise.all([root.fetch("https://a.bilivideo.com/x.m4s", piece), root.fetch("https://b.bilivideo.com/x.m4s", piece)]);
  assert.equal(slots.length, 2, "two pieces in flight take two slots");
  assert.deepEqual(slots.map(s => s.busy), [true, true]);
  assert.equal(a.status, 206); assert.equal(a.headers.get("content-range"), "bytes 0-9/10", "status and headers pass through");
  assert.equal(await readAll(a), 1500, "the downloader gets every byte"); assert.equal(await readAll(b), 1500);
  assert.deepEqual(slots.map(s => [s.bytes, s.busy, s.host]), [[1500, false, "a.bilivideo.com"], [1500, false, "b.bilivideo.com"]], "bytes counted, slots freed at the end");

  await readAll(await root.fetch("https://c.bilivideo.com/x.m4s", piece));
  assert.equal(slots.length, 2, "a free slot is reused");
  assert.deepEqual([slots[0].bytes, slots[0].run], [1500, 2], "taken for another node, the slot starts a new run with its own count");
  await readAll(await root.fetch("https://c.bilivideo.com/x.m4s", piece));
  assert.deepEqual([slots[0].bytes, slots[0].run], [3000, 2], "the same node again goes on counting in the same run");

  await readAll(await root.fetch("https://c.bilivideo.com/x.m4s", { headers: { Range: "bytes=0-9" } }));
  await readAll(await root.fetch("https://api.bilibili.com/x", {}));
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
  await readAll(stale);
  assert.equal(staleSlot.bytes, before, "the old stream's late bytes do not count for the new owner");
  assert.equal(staleSlot.busy, true, "the old stream's end does not free the new owner's slot");
  await readAll(fresher); Date.now = realNow;
  assert.equal(staleSlot.busy, false);

  // The poll, through the real poll script: each cell reports its current node's run and bytes.
  const tabPage = { fetch: async url => new Response(new Uint8Array(url.includes("//a.") ? 1000 : 500), { status: 206 }) };
  vm.createContext(tabPage);
  tabPage.__BTR_LOCAL__ = { lease: 0, id: 1, slots: installMeter(tabPage) };
  tabPage.__BTR_DESKTOP__ = { getSettings: () => ({ mode: "custom", customHosts: [] }), getStatus: () => ({ transport: { networkBytes: 0 }, playback: null }) };
  const get = async host => readAll(await tabPage.fetch(`https://${host}.bilivideo.com/x.m4s`, piece));
  const polled = () => vm.runInContext(POLL_SCRIPT, tabPage);
  assert.equal(await get("a"), 1000, "the downloader still gets every byte");
  assert.deepEqual(polled().slots[0], [1000, "a.bilivideo.com", 1]);
  await get("b"); await get("a");
  assert.deepEqual(polled().slots[0], [1000, "a.bilivideo.com", 3], "A -> B -> A: a new run that counts only its own bytes");
  await get("a");
  assert.deepEqual(polled().slots[0], [2000, "a.bilivideo.com", 3], "the same run keeps growing");
  assert.equal(polled().page, 1);
  console.log("thread meter: ok");
})().catch(error => { console.error(error); process.exit(1); });
