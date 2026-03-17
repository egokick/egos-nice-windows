const APP_VERSION = "__APP_VERSION__";
const SHELL_CACHE = `youtube-sync-shell-${APP_VERSION}`;
const DATA_CACHE = `youtube-sync-data-${APP_VERSION}`;
const MEDIA_CACHE = `youtube-sync-media-${APP_VERSION}`;
const SHELL_ASSETS = [
  "/",
  `/styles.css?v=${APP_VERSION}`,
  `/app.js?v=${APP_VERSION}`,
  "/favicon.ico",
];

self.addEventListener("install", (event) => {
  event.waitUntil((async () => {
    const cache = await caches.open(SHELL_CACHE);
    await cache.addAll(SHELL_ASSETS);
    await self.skipWaiting();
  })());
});

self.addEventListener("activate", (event) => {
  event.waitUntil((async () => {
    const expectedCaches = new Set([SHELL_CACHE, DATA_CACHE, MEDIA_CACHE]);
    const cacheNames = await caches.keys();
    await Promise.all(
      cacheNames
        .filter((cacheName) => !expectedCaches.has(cacheName))
        .map((cacheName) => caches.delete(cacheName)),
    );
    await self.clients.claim();
  })());
});

self.addEventListener("fetch", (event) => {
  const request = event.request;
  if (request.method !== "GET") {
    return;
  }

  const url = new URL(request.url);
  if (url.origin !== self.location.origin) {
    return;
  }

  if (url.pathname === "/api/bootstrap") {
    event.respondWith(networkFirst(request, DATA_CACHE));
    return;
  }

  if (
    url.pathname === "/"
    || url.pathname === "/styles.css"
    || url.pathname === "/app.js"
    || url.pathname === "/favicon.ico"
  ) {
    event.respondWith(staleWhileRevalidate(request, SHELL_CACHE));
    return;
  }

  if (url.pathname.startsWith("/api/videos/")) {
    const isThumbnail = url.pathname.endsWith("/thumbnail");
    const isCaptionFile = url.pathname.includes("/captions/") && url.pathname.endsWith("/file");
    const isFullStream = url.pathname.endsWith("/stream") && !request.headers.has("range");

    if (isThumbnail || isCaptionFile || isFullStream) {
      event.respondWith(staleWhileRevalidate(request, MEDIA_CACHE));
    }
  }
});

async function networkFirst(request, cacheName) {
  const cache = await caches.open(cacheName);
  try {
    const response = await fetch(request);
    if (response.ok) {
      await cache.put(request, response.clone());
    }

    return response;
  } catch {
    const cachedResponse = await cache.match(request);
    if (cachedResponse) {
      return cachedResponse;
    }

    throw new Error("Network request failed.");
  }
}

async function staleWhileRevalidate(request, cacheName) {
  const cache = await caches.open(cacheName);
  const cachedResponse = await cache.match(request);
  const networkPromise = fetch(request)
    .then(async (response) => {
      if (response.ok) {
        await cache.put(request, response.clone());
      }

      return response;
    })
    .catch(() => null);

  if (cachedResponse) {
    void networkPromise;
    return cachedResponse;
  }

  const networkResponse = await networkPromise;
  if (networkResponse) {
    return networkResponse;
  }

  throw new Error("Network request failed.");
}
