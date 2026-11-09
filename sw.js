// Service worker for the installed app (website only, not the artifact).
// The page and its scripts are saved for offline use. Coach voice packs are saved one by one
// as they're played, because all of them together are about 250 MB.
const VERSION = "2d14789e05";
const SHELL = "shell-" + VERSION, VOICE = "voice", CDN = "cdn";
const SHELL_FILES = ["./", "manifest.webmanifest", "icon-192.png", "icon-512.png", "icon-maskable-512.png", "voice/index.json"];
const CDN_FILES = ["https://cdnjs.cloudflare.com/ajax/libs/chess.js/0.10.3/chess.min.js"];

self.addEventListener("install", e => {
  e.waitUntil((async () => {
    const c = await caches.open(SHELL);
    await c.addAll(SHELL_FILES.map(u => new Request(u, { cache: "reload" })));
    const cdn = await caches.open(CDN);
    // cache.add() refuses cross-site (opaque) answers, so fetch and store them by hand
    for (const u of CDN_FILES) if (!(await cdn.match(u))) { try { const r = await fetch(u, { mode: "no-cors" }); await cdn.put(u, r) } catch (err) {} }
    await self.skipWaiting();
  })());
});

self.addEventListener("activate", e => {
  e.waitUntil((async () => {
    for (const k of await caches.keys()) if (k.startsWith("shell-") && k !== SHELL) await caches.delete(k);
    await self.clients.claim();
  })());
});

// Network first, saved copy when offline.
async function networkFirst(req, cacheName, key) {
  const c = await caches.open(cacheName);
  try {
    const r = await fetch(req);
    if (r.ok) await c.put(key || req, r.clone());
    return r;
  } catch (err) {
    const hit = await c.match(key || req, { ignoreSearch: true });
    if (hit) return hit;
    throw err;
  }
}

// The voice index says where each clip sits inside the packs. If it changed, the saved packs
// are out of date, so clear them before the page asks for any.
async function voiceIndex(req) {
  const shell = await caches.open(SHELL), key = "voice/index.json";
  try {
    const r = await fetch(req, { cache: "no-cache" });
    if (r.ok) {
      const fresh = await r.clone().text(), old = await shell.match(key);
      if (old && (await old.text()) !== fresh) await caches.delete(VOICE);
      await shell.put(key, r.clone());
    }
    return r;
  } catch (err) {
    const hit = await shell.match(key);
    if (hit) return hit;
    throw err;
  }
}

// Saved copy first; otherwise download it and keep it.
async function cacheFirst(req, cacheName) {
  const c = await caches.open(cacheName), hit = await c.match(req);
  if (hit) return hit;
  const r = await fetch(req);
  if (r.ok || r.type === "opaque") c.put(req, r.clone());
  return r;
}

self.addEventListener("fetch", e => {
  const req = e.request;
  if (req.method !== "GET") return;
  const url = new URL(req.url);
  if (url.origin === location.origin) {
    if (req.mode === "navigate") return e.respondWith(networkFirst(req, SHELL, "./"));
    if (url.pathname.endsWith("/voice/index.json")) return e.respondWith(voiceIndex(req));
    if (url.pathname.includes("/voice/") && url.pathname.endsWith(".mp3") && !req.headers.has("range")) return e.respondWith(cacheFirst(req, VOICE));
    return e.respondWith(networkFirst(req, SHELL));
  }
  if (url.hostname === "cdnjs.cloudflare.com") return e.respondWith(cacheFirst(req, CDN));
});
