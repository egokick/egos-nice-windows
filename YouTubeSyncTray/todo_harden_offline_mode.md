# Harden Offline Mode

## Goal

Make the YouTube Sync app behave like an offline-first library browser.

Primary requirements:

- Downloaded videos must remain easy to browse and play while offline.
- Switching between downloaded-account libraries must keep working while offline.
- Opening the web UI must not depend on live YouTube access.
- Network and auth work must never interrupt normal offline library use.

Non-goals:

- Downloading from YouTube while offline.
- Refreshing Watch Later totals while offline.
- Requiring live browser cookies just to open or switch local libraries.

## Main Direction

The local downloaded library must become the source of truth.

Today the app mostly derives the UI from:

- live browser account discovery
- live/persisted YouTube account discovery
- yt-dlp sidecar metadata

That is backwards for offline use. The correct priority order is:

1. local account/library scopes on disk
2. local library catalog on disk
3. last-known browser/UI snapshot in the browser
4. live browser/YouTube discovery when available

## Design Changes

### 1. Make Library Scope the Primary Offline Unit

Create a persisted store, tentatively:

- `%LocalAppData%/YouTubeSyncTray/known-library-scopes.json`

Each scope record should include:

- `scopeKey`
- `folderName`
- `downloadsPath`
- `thumbnailCachePath`
- `archivePath`
- `browserAccountKey`
- `browserDisplayName`
- `browserEmail`
- `browserProfile`
- `browserAuthUserIndex`
- `youTubeAccountKey`
- `youTubeDisplayName`
- `youTubeHandle`
- `youTubeAuthUserIndex`
- `downloadedVideoCount`
- `lastSeenAtUtc`
- `lastSuccessfulSyncAtUtc`
- `isAvailableOnDisk`

Rules:

- Every resolved account scope should be registered locally.
- The web UI should render library/account switching from this local scope store first.
- Live browser and YouTube discovery should only enrich labels, avatars, and auth metadata.
- If live discovery is unavailable, known local scopes must still be switchable.

Why:

- The actual storage unit already exists in `AccountScopeResolver`.
- Offline switching should choose a local folder, not require current cookies or YouTube account discovery.

### 2. Persist the Last Selected YouTube Account Per Browser Account

Today browser-account switching can clear the selected YouTube account on an offline miss and fall back to browser scope.

Change the selection rules:

- Persist `browserAccountKey -> lastSelectedYouTubeAccountKey`.
- When switching browser accounts, restore the remembered YouTube account if that scope exists locally.
- If no remembered YouTube account exists, choose the most recent local YouTube scope for that browser account.
- Only fall back to browser-only scope if there is truly no local YouTube-scoped library for that browser account.
- Never clear `SelectedYouTubeAccountKey` just because offline discovery returned no accounts.
- Mark the selection as stale/offline if discovery is unavailable instead of deleting it.

This should remove the "library disappeared after offline account switch" failure mode.

### 3. Replace the Single Cookie-Dependent YouTube Account Cache

Current behavior is too fragile:

- discovery depends on `youtube-cookies.txt`
- discovery depends on cookie metadata matching the current browser/profile
- discovery persists to a single global cache file

Replace that with:

- per-browser/profile discovery cache files, or
- better, embed discovery metadata into the known-scope store

Rules:

- Cached YouTube account entries must survive reloads.
- Cached YouTube account entries must not disappear just because the current cookie export is missing.
- Local downloaded-scope entries must remain visible even if browser cookies are gone.

Live YouTube discovery should be optional enrichment, not a gate for offline switching.

### 4. Add a Durable Local Library Catalog

Create a per-scope catalog, tentatively:

- `%LocalAppData%/YouTubeSyncTray/library-catalog/<scopeFolderName>.json`

Each item should store:

- `videoId`
- `title`
- `uploaderName`
- `videoPath`
- `thumbnailPath`
- `captionTracks`
- `playlistIndex`
- `lastSeenAtUtc`
- `sourceInfoPath`

Load order for the library:

1. catalog
2. `*.info.json`
3. raw video filename fallback

Rules:

- A playable video on disk should not disappear from the library just because the `.info.json` file is missing or damaged.
- After every sync and every successful disk scan, refresh the catalog.
- The catalog should be the fast path for `/api/videos`.

Benefits:

- Better offline reliability
- Faster library startup
- Stable video counts for the scope selector
- Less dependence on yt-dlp sidecar integrity

## Web UI Offline Hardening

### 5. Make the Browser Shell Cacheable

Current cache behavior is too aggressive. The app shell and all API responses are effectively non-cacheable.

Split caching by route:

- Keep `no-store` for:
  - `/api/status`
  - `/api/settings`
  - `/api/settings/*`
  - `/api/sync`
  - `/api/remove`
  - `/api/restore`
  - account-selection commands
- Make cacheable:
  - `/`
  - `/app.js`
  - `/styles.css`
  - `/favicon.ico`
  - video thumbnails
  - caption files
  - video streams

For cacheable media:

- add `ETag`
- add `Last-Modified`
- allow normal HTTP/browser caching

For shell assets:

- version them by assembly version or file hash
- use long-lived immutable caching

### 6. Add a Service Worker for Desktop Localhost Use

The local browser host already defaults to `tom.localhost`, which is a good base for a desktop service worker.

Add:

- `web-ui/sw.js`

Responsibilities:

- precache shell assets
- cache the last successful bootstrap payload
- cache thumbnails and caption files
- optionally cache recently played media ranges
- serve the last known library snapshot when the tray app is temporarily unavailable

Important note:

- Service worker support should be aimed at the laptop/local-host path.
- Phone/LAN clients over plain HTTP may not get the same service-worker behavior.
- Those clients should still benefit from relaxed cache headers and browser HTTP caching.

### 7. Add a Bootstrap Snapshot for Fast Offline Restore

Add a combined endpoint, tentatively:

- `/api/bootstrap`

Payload should include:

- current status
- current video list
- available known scopes
- selected scope
- available browser accounts
- available YouTube accounts
- snapshot timestamps

Browser behavior:

- save the last successful bootstrap payload in IndexedDB
- on load, render the cached snapshot immediately
- revalidate in the background
- if the tray app is temporarily unavailable, stay usable in stale mode

This allows:

- quick UI startup
- better reload behavior
- graceful recovery during tray restarts

## Stop Online Work From Interrupting Offline Browsing

### 8. Separate "Browse Library" From "Sync With YouTube"

Offline browsing should never automatically trigger auth or sync flows.

Change startup behavior:

- always start the local web server first
- always load the local library first
- do not auto-run sync just to open the library

Change auth behavior:

- never auto-open managed browser sign-in while the user is just browsing
- only do fresh cookie export when the user explicitly starts sync

Change settings behavior:

- do not auto-refresh Watch Later totals on settings open
- show the last successful total and its timestamp
- keep `Refresh Total` as an explicit user action

Recommended product behavior:

- "Browse downloaded library" is always available
- "Sync Now" is explicit
- "Refresh Total" is explicit
- auth prompts only appear for explicit sync actions

### 9. Consider an Explicit Offline/Library-Only Mode

Optional but recommended:

- add a visible "Offline Library Mode" state in the UI

When active:

- suppress background account refresh
- suppress background Watch Later refresh
- suppress any auth/cookie prompts
- continue serving local library and playback

This can be automatic when internet is unavailable, manual, or both.

## Browser Profile Discovery

### 10. Broaden Sync-Source Discovery

Current browser account discovery is too narrow because it only checks the current profile and `Default`.

Improve it by:

- enumerating all Chromium profile directories with a `Preferences` file
- reading profile labels where possible
- merging discovered profiles with known local library scopes

Important distinction:

- the primary library switcher should come from local scopes
- the settings screen can still expose live browser/profile discovery for sync configuration

This avoids tying offline library access to live profile detection.

## Suggested New Components

Potential new files:

- `KnownLibraryScopeStore.cs`
- `LibraryCatalogStore.cs`
- `NetworkModeService.cs` or `ReachabilityService.cs`
- `web-ui/sw.js`

Potential existing touch points:

- `AccountScopeResolver.cs`
- `LibraryWebServer.cs`
- `TrayApplicationContext.cs`
- `YouTubeAccountDiscoveryService.cs`
- `BrowserAccountDiscoveryService.cs`
- `VideoItem.cs`
- `SyncService.cs`
- `web-ui/app.js`
- `web-ui/index.html`

## Implementation Order

### Phase 1: Fix the Offline Account-Switching Breakages

- Add `KnownLibraryScopeStore`
- persist `browser -> last selected YouTube account`
- make library switching local-first
- stop clearing selected YouTube account on offline discovery miss

Expected result:

- account switching works offline using downloaded libraries already on disk

### Phase 2: Make Library Enumeration Durable

- add `LibraryCatalogStore`
- load from catalog before sidecars
- add raw video-file fallback

Expected result:

- downloaded videos stay visible even if sidecar metadata is incomplete

### Phase 3: Make the Web UI Reload-Resilient

- add `/api/bootstrap`
- add IndexedDB snapshot storage
- relax cache policy for shell and media
- add service worker for desktop localhost use

Expected result:

- browser reloads and brief tray interruptions become much less disruptive

### Phase 4: Remove Background Online Interruptions

- stop auto sync on browse-first startup
- stop auto summary refresh on settings open
- gate auth prompts behind explicit sync

Expected result:

- offline browsing no longer causes auth or network workflows to pop up unexpectedly

### Phase 5: Improve Sync-Source Discovery

- enumerate all local Chromium profiles
- merge profile discovery with known local scopes

Expected result:

- sync configuration becomes more reliable for multi-profile users

## Acceptance Criteria

The offline hardening work is done when all of these are true:

- Opening the library while offline never requires live YouTube access.
- Switching between previously downloaded account libraries works offline after a full page reload.
- Browser-account changes do not hide an existing local YouTube-scoped library.
- Missing `.info.json` files do not automatically remove playable videos from the library.
- The settings panel does not trigger network/auth work unless the user explicitly asks for it.
- Reloading the page while the tray app is briefly unavailable still shows the last known library state.
- When the tray app comes back, the UI rehydrates without losing the user-selected account/library.

## Recommended Testing

Add integration tests for:

- browser-account switch while YouTube discovery cache is unavailable
- offline YouTube-account switch using only known local scopes
- multi-profile offline switching
- missing `.info.json` with video file still present
- bootstrap snapshot restore when `/api/status` is temporarily unavailable
- settings open while offline does not trigger Watch Later refresh
- startup while offline does not trigger sign-in flow unless sync is explicitly requested
