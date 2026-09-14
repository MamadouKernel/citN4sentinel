const CACHE_STATIC = 'n4-sentinel-static-v5';
const CACHE_RUNTIME = 'n4-sentinel-runtime-v5';

const STATIC_ASSETS = [
    '/',
    '/manifest.json',
    '/favicon.svg',
    '/css/tailwind.min.css',
    '/app.css',
    '/js/sw-register.js',
    '/js/password-toggle.js',
    '/js/topology-edges.js',
    '/js/dictee.js',
    '/js/n4VoiceCopilot.js',
    '/js/idle-timeout.js'
];

const STATIC_PREFIXES = [
    '/_framework/',
    '/images/',
    '/css/',
    '/js/'
];

self.addEventListener('install', event => {
    event.waitUntil(
        caches.open(CACHE_STATIC).then(cache => cache.addAll(STATIC_ASSETS))
    );
    self.skipWaiting();
});

self.addEventListener('activate', event => {
    event.waitUntil(
        caches.keys().then(keys =>
            Promise.all(
                keys
                    .filter(k => k !== CACHE_STATIC && k !== CACHE_RUNTIME)
                    .map(k => caches.delete(k))
            )
        )
    );
    self.clients.claim();
});

function IsStaticAsset(url) {
    const pathname = new URL(url).pathname;
    for (const p of STATIC_PREFIXES)
        if (pathname.startsWith(p)) return true;
    return STATIC_ASSETS.includes(pathname);
}

self.addEventListener('fetch', event => {
    const req = event.request;
    if (req.method !== 'GET') return;

    // Assets statiques : Cache d'abord, fallback réseau, fallback runtime cache.
    if (IsStaticAsset(req.url)) {
        event.respondWith(
            caches.match(req).then(cached => cached ?? fetch(req).then(async resp => {
                if (resp.ok && resp.type !== 'opaque') {
                    const clone = resp.clone();
                    caches.open(CACHE_RUNTIME).then(c => c.put(req, clone)).catch(() => { });
                }
                return resp;
            }).catch(() => cached))
        );
        return;
    }

    // Pages HTML, API, Blazor interactive : réseau d'abord, cache en fallback.
    event.respondWith(
        fetch(req).then(resp => {
            if (resp.ok && resp.type !== 'opaque' && (req.mode === 'navigate' || req.destination === 'document')) {
                const clone = resp.clone();
                caches.open(CACHE_RUNTIME).then(c => c.put(req, clone)).catch(() => { });
            }
            return resp;
        }).catch(() => caches.match(req).then(m => m ?? caches.match('/')))
    );
});
