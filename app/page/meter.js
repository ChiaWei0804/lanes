// Page-side code, run inside the Bilibili client's player page (not in Lanes.exe). Lanes.exe embeds this file.
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
        const now = Date.now(), host = new URL(String(input)).hostname;
        slot = slots.find(item => !item.busy || now - item.at > STALE_MS);
        if (!slot) { slot = { bytes: 0, run: 0 }; slots.push(slot); }
        // Taken for another node, a slot starts a new run with its own count, so the window never shows one node's
        // bytes under another; the same node again goes on counting.
        if (slot.host !== host) { slot.run += 1; slot.bytes = 0; slot.host = host; }
        // A stale slot may still have its old stream alive: only the current owner counts or frees it.
        owner = {}; slot.owner = owner;
        slot.busy = true; slot.at = now;
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
