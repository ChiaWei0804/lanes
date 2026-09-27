// Page-side code, run inside the Bilibili client's player page (not in Lanes.exe). Lanes.exe embeds this file and fills
// in the marked places: Lanes' version, the meter (meter.js), the bundle (BTR's 8 page files from vendor/btr,
// unmodified) and Lanes' settings when the page is registered.
// Only the player page, only its top frame, and never on top of an installed BTR desktop.
// When Lanes stops renewing the lease (closed or killed), new requests go native within 6 s; running ones finish.
if (window === top && location.origin === "https://bilipc.bilibili.com" && location.pathname === "/player.html" && !globalThis.__BTR_DESKTOP__) {
globalThis.__BTR_DESKTOP_RELEASE__ = { version: "0.9.4.2-d1+lanes-{{version}}", adapterRevision: 1 };
globalThis.__BTR_LOCAL__ = { lease: Date.now(), id: Date.now() + Math.random(), slots: ({{meter}})(globalThis) };
{{bundle}}
// Lanes' settings from the first request on; BTR would start with the ones it stored, which are the last ones Lanes
// pushed to a page. Only a window's first document: a reload keeps BTR's stored ones, which Lanes' pushes keep current.
try { if (!sessionStorage.getItem("lanes.settings")) { sessionStorage.setItem("lanes.settings", "1"); {{settings}}; } } catch (e) {}
setInterval(() => { const api = globalThis.__BTR_DESKTOP__; if (api && Date.now() - globalThis.__BTR_LOCAL__.lease > 6000 && api.getSettings().enabled) api.setSettings({ enabled: false }); }, 1000);
}
