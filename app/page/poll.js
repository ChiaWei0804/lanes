// Page-side code, run inside the Bilibili client's player page (not in Lanes.exe). Lanes.exe embeds this file.
// Renews the lease and reads BTR's own status in one round trip. null: no BTR here; foreign: BTR desktop.
(() => {
  const local = globalThis.__BTR_LOCAL__, api = globalThis.__BTR_DESKTOP__;
  if (!api) return null;
  if (!local) return { foreign: true };
  local.lease = Date.now();
  const status = api.getStatus?.(), t = status?.transport, s = api.getSettings(), media = status?.playback;
  const hostsOk = s.mode !== "custom" || s.customHosts.join() === (globalThis.__BILI_CDN_RESOLVER_FACTORY__?.GLOBAL_HOSTS || []).join();
  return t ? { playing: !!media && !media.paused, settings: { enabled: s.enabled, mode: s.mode, autoConcurrency: s.autoConcurrency, concurrency: s.concurrency, hostsOk }, bytes: t.networkBytes, requests: t.acceleratedRequests, fallbacks: t.fallbackRequests, active: t.activeThreads, threads: t.threads, suspended: t.suspended, page: local.id, slots: (local.slots || []).map(slot => [slot.bytes, slot.host || "", slot.run]) } : null;
})()
