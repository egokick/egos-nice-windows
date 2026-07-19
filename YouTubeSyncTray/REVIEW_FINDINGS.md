# YouTube Sync App Review Findings

This is a living review log. Findings are ranked **Critical**, **High**, **Medium**, or **Low** and include only issues supported by code or runtime evidence.

## Summary

| ID | Severity | Status | Finding |
|---|---|---|---|
| YTS-002 | Medium | Open | Playback progress from the supported phone UI is always rejected and silently lost |
| YTS-003 | High | **Complete** | A partial backup failure during redownload can permanently delete original video files |
| YTS-004 | Medium | Open | Stale or partially generated thumbnails can remain permanently cached |
| YTS-005 | Medium | **Complete** | Non-atomic JSON persistence can silently reset settings and library state after interruption |
| YTS-006 | Medium | Open | Startup and local browsing can trigger online account work even when automatic sync is paused |
| YTS-007 | Medium | **Complete** | A transient empty playlist response can erase cached order and pause automatic sync |
| YTS-008 | High | **Complete** | Any yt-dlp output containing the word `cookies` can be misclassified as auth failure and killed |
| YTS-009 | Low | Open | A single failure can append megabytes to an unbounded tray log |
| YTS-010 | Medium | **Complete** | Redownloaded videos and captions reuse cache-fresh URLs and can display old content |
| YTS-011 | Low | Open | Failed or skipped sync targets can remain indefinitely labeled as pending downloads |
| YTS-012 | Medium | **Complete** | One sync redundantly enumerates the same Watch Later playlist several times |
| YTS-013 | Medium | Open | Pending placeholders are counted as downloaded videos in library summaries |
| YTS-014 | Low | Open | A no-result search displays a contradictory empty-library explanation |
| YTS-015 | Low | Open | Clear Selection stays enabled when no videos are selected |
| YTS-016 | Medium | Open | The phone UI enables laptop-only controls that are guaranteed to fail |
| YTS-017 | Medium | Open | One migration error can permanently strand part of an existing library |
| YTS-018 | High | **Complete** | Ordinary titles containing `Sign in` can still be treated as auth failure and kill yt-dlp |
| YTS-019 | Medium | Open | Cookie recovery waits for one browser to close and then unexpectedly opens a second browser |
| YTS-020 | Medium | Open | Choosing a different recovery browser does not update the account context or persist the choice |
| YTS-021 | Medium | Open | Cache-only account reads can indefinitely suppress live YouTube account refreshes |
| YTS-022 | Medium | Open | The app reports automatic sync as armed or paused, but no automatic sync is scheduled |
| YTS-023 | Low | Open | Library-version polling never triggers an immediate video refresh |
| YTS-024 | Low | Open | Phone-access status polling repeatedly launches full PowerShell processes |

## Findings

### YTS-002 — Playback progress from the supported phone UI is always rejected and silently lost

**Severity:** Medium  
**Category:** Functional correctness / data loss  
**Status:** Confirmed by end-to-end code-path inspection

The app explicitly advertises phone access and permits non-local clients to load and stream videos. The same web client schedules and sends playback progress for every player, regardless of whether it is running on the laptop or a phone. However, the `/api/videos/progress` endpoint returns 403 whenever `IsLocalRequest` is false. The JavaScript catches all persistence failures without notifying the viewer.

Consequently, playback on a phone appears normal and locally updates the card, but the server never stores the position. A refresh or another device will show the old position, and watching past the UI's watched threshold will not persist as expected.

**Evidence:**

- `LibraryWebServer.cs:289-293` exposes video/media reads to remote clients.
- `LibraryWebServer.cs:376-400` rejects progress writes from every non-local client.
- `web-ui/app.js:1494-1509` schedules progress persistence for the shared player with no remote-client exclusion.
- `web-ui/app.js:1562-1582` sends the request and silently discards all failures, including the guaranteed phone-side 403.

**Suggested remediation:** Permit phone clients to save playback progress through the app's intended phone-access mechanism. If phone viewing is intentionally read-only, clearly state that progress will not be saved and avoid presenting optimistic local state as durable.

### YTS-003 — A partial backup failure during redownload can permanently delete original video files

**Severity:** High  
**Category:** Data loss / error handling  
**Status:** Complete — fixed and regression-tested on 2026-07-19

Redownload first moves every existing file in each selected video bundle into a temporary backup directory. The returned backup manifest is assigned only after `BackupExistingVideoBundles` completes successfully. If any `Directory.CreateDirectory` or `File.Move` throws after one or more files have already moved, the assignment never occurs and the caller's `backups` list remains empty.

The catch block consequently restores nothing. The finally block then recursively deletes the backup directory, including the original files that were successfully moved before the exception. A routine locked sidecar, path failure, permissions issue, or storage I/O error can therefore turn a failed redownload attempt into permanent loss of previously valid media/metadata.

**Evidence:**

- `SyncService.cs:361-371` initializes an empty manifest and assigns it only from the completed return value of `BackupExistingVideoBundles`.
- `SyncService.cs:452-459` attempts restoration from that still-empty manifest, then always deletes the backup root.
- `SyncService.cs:1324-1356` moves files incrementally but adds each completed bundle to a method-local manifest only after all of that bundle's moves succeed.

**Suggested remediation:** Make backup creation transactional. Record each successful move in a caller-owned manifest immediately, restore that manifest on any failure, and delete the backup root only after verifying that restoration or redownload succeeded. Prefer copying plus verified fsync/hash before deleting originals, or use a scoped transaction object whose disposal restores uncommitted moves.

### YTS-004 — Stale or partially generated thumbnails can remain permanently cached

**Severity:** Medium  
**Category:** Cache correctness / UI reliability  
**Status:** Confirmed by control-flow inspection

`EnsureThumbnailAsync` initially compares the cache and source timestamps. If the cache is older it correctly proceeds toward regeneration. After acquiring the semaphore, however, it returns immediately whenever the cache file merely exists, without repeating the freshness test. Thus an existing stale thumbnail can never be regenerated.

The ffmpeg output is also written directly to the final cache path. If ffmpeg fails after creating a zero-byte or partial JPEG, the file is left in place. Its new modification time normally makes the initial check accept it on every subsequent request, permanently turning a transient conversion failure into a broken thumbnail.

**Evidence:**

- `ThumbnailCacheService.cs:21-29` detects freshness using source/cache timestamps.
- `ThumbnailCacheService.cs:42-48` discards that result after waiting and treats any existing file as valid.
- `ThumbnailCacheService.cs:50-85` writes ffmpeg output directly to the final path and does not delete it on nonzero exit or validate that it is nonempty/decodable.

**Suggested remediation:** Re-run the same freshness/validity check inside the semaphore. Generate to a unique temporary file, verify successful exit plus a nonzero valid image, then atomically replace the cache path. Delete temporary output on every failure/cancellation path.

### YTS-005 — Non-atomic JSON persistence can silently reset settings and library state after interruption

**Severity:** Medium  
**Category:** Reliability / state loss  
**Status:** Complete — fixed and regression-tested on 2026-07-19

**Resolution:** All affected JSON stores now write through a flushed same-directory temporary file and atomically replace the destination. A last-known-good `.bak` is retained, loaders recover from it when the primary file is unreadable or malformed, and a malformed primary cannot overwrite a valid backup.

Settings, per-video watched/progress state, known account scopes, Watch Later order, account discovery cache, and catalog metadata are written directly over their sole JSON file with `File.WriteAllText`. An app termination, power loss, full disk, or I/O failure during the write can leave a truncated or malformed file. Most corresponding load methods catch every parse/read error and silently return a brand-new empty/default model, with neither a backup recovery attempt nor a visible warning.

The next ordinary update then writes that empty/default model back over the damaged file, making loss of settings, watched positions, scope/account associations, or ordering permanent. Media files remain on disk, but the app can appear reset or can forget substantial user state.

**Evidence:**

- `AppSettings.cs:75-98` silently defaults on any load error and overwrites the sole settings file directly.
- `LibraryVideoStateStore.cs:185-211` silently treats malformed playback state as empty and directly overwrites it on the next change.
- `KnownLibraryScopeStore.cs:218-242` applies the same pattern to the complete known-scope registry.
- `WatchLaterOrderStore.cs:80-111`, `LibraryCatalogStore.cs:59-106`, and `YouTubeAccountDiscoveryService.cs:598-650` use the same single-file, non-atomic pattern.

**Suggested remediation:** Serialize to a same-directory temporary file, flush it, then atomically replace the destination while retaining a last-known-good backup. On parse failure, preserve the corrupt file, attempt backup recovery, log the failure, and surface a clear user warning instead of silently accepting an empty model.

### YTS-006 — Startup and local browsing can trigger online account work even when automatic sync is paused

**Severity:** Medium  
**Category:** Offline reliability / unexpected behavior  
**Status:** Confirmed by call-chain inspection; the broader offline-mode concern is also acknowledged in `todo_harden_offline_mode.md`

`QueueWatchLaterTotalRefresh` correctly refuses background work while the app is busy or automatic sync is disarmed. `QueueWatchLaterOrderRefresh` does neither. Initialization explicitly queues an order refresh, as do account/settings and library refresh paths. That task calls `SyncService.GetWatchLaterOrderedIdsAsync`, which prepares live authentication and, when no matching saved cookie export exists, enters the managed Chromium sign-in/export flow.

The result is that starting the app, selecting a local library, or saving settings can perform network work and open a browser sign-in window even though automatic sync is paused and the user only intends to view existing downloads. It also creates avoidable background contention with foreground operations.

**Evidence:**

- `TrayApplicationContext.cs:205-212` queues Watch Later order refresh during startup.
- `TrayApplicationContext.cs:1744-1761` guards total refresh with `_isBusy` and `AutoSyncArmed`.
- `TrayApplicationContext.cs:1764-1782` queues order refresh without either guard.
- `TrayApplicationContext.cs:1844-1855` invokes the live sync service for that background refresh.
- `SyncService.cs:76-91` and `SyncService.cs:463-491` prepare authentication and can launch the managed sign-in flow.

**Suggested remediation:** Make local startup and library/account switching cache-only. Gate background order refresh behind the same explicit online/automatic-sync policy as total refresh, and only export fresh cookies or open a login browser after a user-initiated sync/refresh command.

### YTS-007 — A transient empty playlist response can erase cached order and pause automatic sync

**Severity:** Medium  
**Category:** Sync state correctness / resilience  
**Status:** Complete — fixed and regression-tested on 2026-07-19

`GetWatchLaterVideosAsync` accepts exit code 0 with zero parsed rows as a valid empty playlist. The background order refresh immediately persists that empty list, overwriting the last-known-good Watch Later order. In the manual sync path, zero targets return a summary with `MissingAfterSyncCount = 0`; `ShouldPauseAutomaticSync` checks only that field, so it disarms automatic sync even though no target was actually validated.

An empty stdout can result from a transient extractor/parser/account response rather than a genuinely empty Watch Later playlist. The live log recorded `GetWatchLaterVideosAsync returned 0 videos` at startup on 2026-07-19, demonstrating that the condition occurs in this installation. Once it happens, cached ordering is lost; if it occurs during manual sync, the app reports no videos and keeps automatic sync paused until the user manually starts another sync.

**Evidence:**

- `SyncService.cs:1216-1252` only rejects nonzero exit codes and returns an empty list when no stdout lines parse.
- `TrayApplicationContext.cs:1844-1855` unconditionally saves the returned list, including empty, as the complete cached order.
- `SyncService.cs:216-228` converts an empty target result into a successful zero-missing summary.
- `TrayApplicationContext.cs:787-800` and `TrayApplicationContext.cs:843-844` pause automatic sync solely when missing count is zero, including a zero-target result.
- `%LOCALAPPDATA%/YouTubeSyncTray/logs/tray-sync.log:2501` recorded the live empty result.

**Suggested remediation:** Treat unexpected empty results as indeterminate unless a separately validated playlist total is also zero. Preserve the last-known-good order on indeterminate/empty refreshes, surface a warning, and require `TargetCount > 0` (or a positively confirmed empty playlist) before declaring sync satisfied and disarming automatic sync.

### YTS-008 — Any yt-dlp output containing the word `cookies` can be misclassified as auth failure and killed

**Severity:** High  
**Category:** Core sync correctness / false failure detection  
**Status:** Complete — fixed and regression-tested on 2026-07-19

The authentication-failure classifier returns true when any output line contains the bare substring `cookies`, without requiring an error/warning context or a known cookie failure phrase. `RunProcessAsync` applies that classifier to every stdout and stderr line while yt-dlp is running and kills the entire process immediately on a match.

Valid output can easily contain the word—for example, a video title, a destination filename, metadata/description, or playlist JSON. The live tray log contains a 2,109,071-character valid Watch Later JSON line followed by the app's authentication-failure guidance and a failed total refresh, demonstrating this exact false-positive class in normal operation. The same predicate can abort an actual media sync when a progress/destination line includes a title containing “cookies,” then needlessly opens the fresh-cookie browser flow.

**Evidence:**

- `SyncService.cs:1088-1111` classifies a line as auth failure on the unrestricted condition `line.Contains("cookies", ...)`.
- `SyncService.cs:730-776` runs the classifier against every stdout/stderr line and kills yt-dlp on the first match.
- `SyncService.cs:596-613` treats the result as authentication failure and initiates fresh cookie export/retry.
- `%LOCALAPPDATA%/YouTubeSyncTray/logs/tray-sync.log:2136-2148` records the failed total refresh plus a 2.1 MB valid playlist JSON response that was included in the false-auth path.

**Suggested remediation:** Match only specific, anchored yt-dlp error/warning signatures that unambiguously indicate unreadable, missing, expired, or rejected cookies. Never classify arbitrary stdout metadata/progress as authentication failure, and avoid killing a process based on a generic noun. Add negative tests containing `cookies` in titles, filenames, JSON metadata, and normal informational messages.

### YTS-009 — A single failure can append megabytes to an unbounded tray log

**Severity:** Low  
**Category:** Diagnostics / disk and tooling performance  
**Status:** Confirmed by code and live runtime evidence

Failure messages include the last 25 newline-delimited yt-dlp output records, but impose no byte or per-record limit. Playlist JSON is emitted as one line, so a single “tail” record can contain the entire multi-megabyte playlist. `TrayLog` then appends that exception text to a log that has no size cap or rotation.

The live log contains one 2,109,071-character JSON line from a failure. That one record accounts for roughly 90% of the current 2.3 MB log and caused a normal tail read to stall until its 30-second timeout during this review. Repeated failures can grow the file without bound and make diagnostics increasingly slow or unwieldy.

**Evidence:**

- `SyncService.cs:823-833` limits by line count only, not by bytes or individual line length.
- `SyncService.cs:836-867` embeds that unbounded tail in the thrown exception message.
- `TrayLog.cs:7-21` appends complete messages forever with no rotation/retention policy.
- `%LOCALAPPDATA%/YouTubeSyncTray/logs/tray-sync.log:2148` is a 2,109,071-character raw JSON line.

**Suggested remediation:** Cap each captured line and the total diagnostic tail by bytes, summarize structured playlist JSON, and rotate the tray log by size/date with a small retention count. Keep full process output only in the overwrite-on-each-run `latest-sync.log` if needed.

### YTS-010 — Redownloaded videos and captions reuse cache-fresh URLs and can display old content

**Severity:** Medium  
**Category:** Cache invalidation / redownload correctness  
**Status:** Complete — fixed and regression-tested on 2026-07-19

**Resolution:** Video streams and individual caption tracks now carry revisions derived from file length and modification time. Caption manifests also carry a revision derived from the complete caption set, so replacements produce new URLs instead of reusing cache-fresh responses.

Thumbnail URLs include a source revision query, but stream and caption URLs are stable for the lifetime of a video ID/track key. The server marks those file/text responses `public, max-age=86400`. Caption files are also placed in the service worker's stale-while-revalidate media cache under that same stable URL.

When redownload replaces a video or subtitle sidecar, its ETag changes but clients are allowed to reuse the still-fresh old response without revalidation for up to 24 hours. The service worker can further return its old caption response immediately. A successful “redownload at best quality” can therefore appear not to work, or can show subtitles that no longer match the replacement video.

**Evidence:**

- `LibraryWebServer.cs:590-598` adds a revision only to `thumbnailUrl`; `streamUrl` and `captionsUrl` remain stable.
- `LibraryWebServer.cs:650-658` creates stable caption-file URLs without a content revision.
- `LibraryWebServer.cs:980-1028` marks ordinary file/text responses public and fresh for 86,400 seconds.
- `web-ui/sw.js:59-65` caches caption files by their unchanged request URL using stale-while-revalidate.

**Suggested remediation:** Add a revision derived from the selected video/caption file length and last-write time (or a content hash) to stream and caption URLs, just as thumbnails do. Alternatively require revalidation (`no-cache`/`max-age=0`) for mutable media sidecars and explicitly invalidate relevant service-worker entries after redownload.

### YTS-011 — Failed or skipped sync targets can remain indefinitely labeled as pending downloads

**Severity:** Low  
**Category:** UI state lifecycle / misleading status  
**Status:** Confirmed against the concurrent pending-card working-tree changes

The new pending-card state stores the full target list and filters out targets as their downloads appear on disk. The target state is cleared only when a broader account/order/auth reset occurs. Normal sync completion, non-cancellation failure, and item-level download failures do not clear or reclassify the remaining targets.

Consequently, unavailable, DRM-only, region-blocked, or otherwise failed videos remain in the library indefinitely with the label “Pending download” after `_isBusy` is false and no operation is running or scheduled. This makes a completed failed/skipped item indistinguishable from an active download.

**Evidence:**

- `LibraryBrowserState.cs:160-200` stores pending targets until `ClearSyncTargetIds` is explicitly called.
- `TrayApplicationContext.cs:613-643` refreshes the library after successful completion without clearing targets.
- `TrayApplicationContext.cs:655-661` also leaves targets intact after a non-cancellation sync failure.
- `TrayApplicationContext.cs:1550-1553` clears targets only for account/order/auth reset conditions.
- `LibraryWebServer.cs:609-627` emits every still-missing stored target as `IsPendingDownload: true`, and `web-ui/app.js:1357-1377` always labels it “Pending download.”

**Suggested remediation:** Track explicit target states such as queued/downloading/failed/skipped/paused. Clear transient pending state at terminal completion, or retain failures with an accurate failure label and retry action. Preserve “pending” only when a resume/retry is genuinely scheduled.

### YTS-012 — One sync redundantly enumerates the same Watch Later playlist several times

**Severity:** Medium  
**Category:** Performance / external-service reliability  
**Status:** Complete — fixed and regression-tested on 2026-07-19

**Resolution:** A sync now takes one flat Watch Later snapshot and derives the total, full order, titles, and configured target range from it. The download phase consumes a temporary batch file of direct video URLs, and the redundant post-sync playlist refresh has been removed.

A manual sync first calls `--dump-single-json` to obtain the total, which materializes metadata for the entire Watch Later playlist. It then launches separate yt-dlp processes to probe successively larger ranges (normally 10, 50, and the configured count), launches yt-dlp again to process/download that same final range, and queues a post-sync full-playlist order refresh. None of these results are shared.

For the live 1,500+ item playlist, the total response alone reached 2.1 MB. Recent logs show a 100-item sync spending roughly 26 seconds in total/probe enumeration before the actual download process started; other runs took longer. The redundant requests increase sync latency, throttling/failure exposure, CPU/process churn, and the chance that different calls observe inconsistent account/playlist state.

**Evidence:**

- `SyncService.cs:180-190` performs the full total request before target discovery.
- `SyncService.cs:99-137` obtains that total by parsing complete `--dump-single-json` output.
- `SyncService.cs:1134-1186` starts a new yt-dlp process for every expanding probe range.
- `SyncService.cs:238-253` starts another playlist process for the final download range.
- `TrayApplicationContext.cs:637` queues another complete order refresh, implemented at `TrayApplicationContext.cs:1844-1855`.
- The live log at 2026-07-19 03:19:32–03:19:58 shows the pre-download sequence for a 100-item sync.

**Suggested remediation:** Perform one flat-playlist enumeration per sync, derive total/order/target metadata from that result, and pass explicit target URLs/IDs to the download phase. Reuse the same snapshot for the post-sync UI state; only refresh again when explicitly requested or stale.

### YTS-013 — Pending placeholders are counted as downloaded videos in library summaries

**Severity:** Medium  
**Category:** UI data correctness  
**Status:** Confirmed against the concurrent pending-card working-tree changes

The server now concatenates missing target placeholders into the same `/api/videos` array as downloaded media. The client stores every entry in `state.videos`, then uses `state.videos.size` as its preferred downloaded count. It does not exclude `isPendingDownload` entries when calculating watched, active, or main-library totals.

During a 100-target sync with 80 files actually present and 20 pending, the headline summary can report 100 videos in the main library even though only 80 are downloaded. Pending entries also inflate the active/unwatched count, contradicting the server's sync-scope counters and making completion progress difficult to interpret.

**Evidence:**

- `LibraryWebServer.cs:581-627` returns downloaded items and pending placeholders in one list.
- `web-ui/app.js:1077-1085` counts watched state across the combined map without excluding pending entries.
- `web-ui/app.js:2139-2156` prefers `state.videos.size` as `downloadedCount` and derives the active/main-library count from it.
- Pending entries are marked by `isPendingDownload`, but that flag is used only for card interaction/rendering at `web-ui/app.js:1357-1407`.

**Suggested remediation:** Keep downloaded items and sync-target placeholders in separate collections, or consistently filter `!video.isPendingDownload` for downloaded/watched/active/library totals. Use the authoritative server `downloadedVideoCount` for headline counts and add separate queued/pending/failed counters.

### YTS-014 - A no-result search displays a contradictory empty-library explanation

**Severity:** Low  
**Category:** UI feedback / search usability  
**Status:** Reproduced in the live web UI and confirmed by code inspection

When a search has no matches, `applyFilters` correctly changes the status line to `No matches for the current search.` The same update then calls `updateEmptyState`, which does not consider the active search term. Because there are no visible cards and the unwatched view is active, it replaces the grid with `No videos in the main library` and says that everything downloaded is already watched or hidden.

The page therefore presents two incompatible explanations for the same empty result. A user can reasonably conclude that their library or watched state changed when the only issue is the current search query.

**Evidence:**

- Live browser reproduction on 2026-07-19: searching for an impossible title produced both `No matches for the current search.` and `No videos in the main library / Everything downloaded here is already marked watched or hidden.` while the unfiltered library contained 98 visible videos.
- `web-ui/app.js:2064-2066` sets the correct no-match status when an active search hides all cards.
- `web-ui/app.js:2813-2833` selects an empty-state explanation using only card count and watched-view state; it never checks `state.searchTerm`.
- `web-ui/app.js:462-467` calls both paths for every search-input change.

**Suggested remediation:** Give an active no-result search its own grid empty state, such as `No videos match this search`, while retaining the actual library summary. Only show the watched/hidden explanation when there is no active search filter.

### YTS-015 - Clear Selection stays enabled when no videos are selected

**Severity:** Low  
**Category:** UI state correctness  
**Status:** Reproduced in the live web UI and confirmed by code inspection

The `Clear Selection` button is not disabled based on whether a selection exists. Its disabled expression is true only while the app is busy *and* the selection is empty, so the button is always enabled whenever the app is idle, including with zero checked cards. Switching between watched and unwatched views clears all selected IDs but leaves the no-op button enabled.

This does not lose data, but it communicates that a selection exists when none does and makes the toolbar state inconsistent with the correctly disabled/hidden bulk actions.

**Evidence:**

- Live browser reproduction on 2026-07-19: after toggling watched and unwatched views, all 100 card checkboxes were unchecked and `state.selectedIds` had been cleared through the toggle handler, but `Clear Selection` remained enabled.
- `web-ui/app.js:454-459` clears selected videos before changing the watched-view filter.
- `web-ui/app.js:2094-2108` computes `hasSelection` and applies it to the other bulk actions, but does not update `clearSelectionButton`.
- `web-ui/app.js:2267` uses `state.isBusy && state.selectedIds.size === 0`; when `state.isBusy` is false, the button is enabled regardless of selection count.

**Suggested remediation:** Disable `Clear Selection` whenever `state.selectedIds.size === 0` (and optionally while busy), and update it in `updateSelectionUi` alongside the other bulk-action controls.

### YTS-016 - The phone UI enables laptop-only controls that are guaranteed to fail

**Severity:** Medium  
**Category:** Phone UI functional correctness / capability handling  
**Status:** Confirmed by end-to-end client/server capability inspection

The remote phone page uses the same controls as the laptop page. The server correctly tells the client that it cannot open the laptop's downloads folder, and the client hides that one button. It does not expose or consume an equivalent capability for the rest of the laptop-only actions. The phone therefore leaves Sync, Settings, browser/YouTube account pickers, card selection, Mark Watched/Unwatched, and Redownload controls available even though every corresponding endpoint rejects non-local requests.

The result is a page that invites phone users to perform actions that can never succeed. Some actions also change optimistic client state before the 403 is surfaced, adding confusion about whether the library was actually changed. This compounds YTS-002: remote playback progress is not the only phone-side write path presented as functional but rejected by the server.

**Evidence:**

- `LibraryWebServer.cs:270-279` passes only a local-request-derived `canOpenDownloadsFolder` flag through bootstrap/status.
- `LibraryWebServer.cs:280-288` and `LibraryWebServer.cs:330-463` reject remote settings, sync, bulk actions, account selection, and settings changes.
- `web-ui/app.js:2239-2241` uses the capability only to hide/disable Open Downloads Folder.
- `web-ui/app.js:2242-2254` renders both account pickers without a remote-management capability.
- `web-ui/app.js:2264-2269` explicitly enables Sync and Settings and enables bulk controls based only on busy/selection state.

**Suggested remediation:** Add an explicit capability such as `canManageLibrary` or `isLocalControlClient` to status/bootstrap. On phone clients, hide or disable every unsupported mutation and account/settings control with a concise `Playback-only phone view` explanation. Alternatively, deliberately support selected remote actions and make the server/client capability contract granular.

### YTS-017 - One migration error can permanently strand part of an existing library

**Severity:** Medium  
**Category:** Upgrade/account migration reliability / library visibility  
**Status:** Confirmed by migration control-flow inspection

Both application-state/download migration and browser-scope-to-YouTube-scope migration use the destination's non-emptiness as their completion test. They copy or move files one at a time under a broad catch. If one operation fails after any earlier file succeeded, the destination is left non-empty. On every later startup or scope resolution, the migration returns immediately and never retries the remaining files.

The original files are generally not deleted by the failing operation, so this is not immediate physical data loss. However, the app switches to and scans only the partly populated destination. The remaining videos, thumbnails, state, or account files can stay indefinitely in the legacy/fallback location and appear lost from the selected library unless the user manually discovers and repairs the split.

**Evidence:**

- `YoutubeSyncPaths.cs:123-140` skips the entire legacy-state migration whenever any destination state exists and wraps the multi-step copy in one catch.
- `YoutubeSyncPaths.cs:145-180` similarly skips download migration when the destination contains any entry and abandons the remaining source candidates after an exception.
- `YoutubeSyncPaths.cs:216-249` migrates files incrementally; if both move and fallback copy fail for one file, the exception escapes after earlier files may already have moved.
- `AccountScopeResolver.cs:132-161` moves root legacy artifacts incrementally, catches once, and will not retry after the first moved artifact makes the scoped folder non-empty.
- `AccountScopeResolver.cs:167-208` has the same non-empty early return around browser-fallback scope migration.

**Suggested remediation:** Track migration completion with an explicit version/marker written only after all planned items are verified. Make each migration idempotent and retry every missing destination file on later starts, even when the destination is partly populated. Record per-file failures visibly and preserve bundle integrity so media, metadata, thumbnails, and captions move together.

### YTS-018 - Ordinary titles containing `Sign in` can still be treated as auth failure and kill yt-dlp

**Severity:** High  
**Category:** Core sync correctness / false failure detection  
**Status:** Complete — fixed and regression-tested on 2026-07-19

**Resolution:** Authentication phrases are eligible for classification only on yt-dlp `ERROR:` or `WARNING:` diagnostic lines. Ordinary JSON metadata, titles, destination filenames, and progress output containing phrases such as `Sign in` no longer terminate the process.

The fix for YTS-008 narrows cookie-related matching, but the authentication classifier still accepts several unrestricted substrings, most notably `Sign in`. It does not require an `ERROR:`/`WARNING:` prefix, stderr origin, or a known complete yt-dlp diagnostic. `RunProcessAsync` applies it to every stdout and stderr line and immediately kills the process on a match.

Watch Later probing deliberately writes each video title to stdout as JSON. A legitimate title containing `Sign in` therefore satisfies the classifier. The same can happen in a normal destination/progress line during media download. The sync is aborted, misreported as an authentication problem, and can trigger an unnecessary managed-browser cookie export despite working credentials.

**Evidence:**

- `SyncService.cs:1123-1140` treats any line containing `Sign in`, `Please sign in`, `confirm you're not a bot`, or `This video may be inappropriate` as authentication failure without validating that the line is a diagnostic.
- `SyncService.cs:763-809` runs that predicate against every stdout and stderr record and kills yt-dlp on the first match.
- `SyncService.cs:1249-1271` emits `[id,title]` JSON for every Watch Later item to stdout, making arbitrary user-controlled titles part of the classifier input.
- `SyncService.cs:247-253` includes titles in download filenames/progress output, providing a second normal-output path to the same false positive.
- Existing regression tests cover ordinary cookie mentions but do not include titles/JSON/destination lines containing the remaining generic auth phrases.

**Suggested remediation:** Classify only complete, known yt-dlp authentication diagnostics in an error/warning context, preferably from stderr. Do not scan metadata JSON or ordinary progress/destination stdout for generic phrases. Add negative tests for JSON titles and filenames containing every auth-related substring, including `Sign in`, `playlist does not exist`, and `confirm you're not a bot`.

### YTS-019 - Cookie recovery waits for one browser to close and then unexpectedly opens a second browser

**Severity:** Medium  
**Category:** Authentication recovery usability / stalled sync  
**Status:** Confirmed by recovery call-chain inspection

Fresh-cookie recovery first calls `PrimeProfileAsync`, which opens a managed browser without remote debugging and waits for that entire browser process to exit. The user must sign in and manually close the window; leaving it open leaves the sync blocked with no timeout. Only after it exits does recovery call `ExportAsync`, which launches another managed browser for the same profile, waits for authenticated cookies, exports them, and closes it.

The second window is surprising and the first wait is unnecessary because the export session already contains its own sign-in detection loop. On first use or expired credentials, a single sync therefore requires two sequential browser launches and can wait indefinitely at the first one even after sign-in succeeded.

**Evidence:**

- `SyncService.cs:686-699` always calls `PrimeProfileAsync` and then `ExportAsync` during a fresh-cookie attempt.
- `ChromiumManagedBrowser.cs:35-75` launches the prime browser and waits for process exit with no sign-in completion detection or bounded timeout.
- `ChromiumCookieExporter.cs:43-71` then starts a separate authenticated managed-browser session for export.
- `ChromiumManagedBrowser.cs:286-318` already detects authentication in that second session and can continue automatically without requiring the window to be closed.

**Suggested remediation:** Use one managed browser session for sign-in detection and cookie export. Continue as soon as authenticated cookies are observed, close that same session after export, and provide a bounded, cancellable timeout with clear status. If a separate priming phase is technically required, explain the second launch and avoid an unbounded wait for manual window closure.

### YTS-020 - Choosing a different recovery browser does not update the account context or persist the choice

**Severity:** Medium  
**Category:** Authentication/account selection correctness  
**Status:** Confirmed by cross-browser recovery state inspection

The recovery dialog explicitly lets the user choose a browser other than the one in current settings. Cookies are exported from the chosen browser, but the app does not update `settings.BrowserCookies`, the selected browser account, or the selected YouTube account. The retry combines cookies from the newly chosen browser with `X-Goog-AuthUser`/`X-Goog-PageId` headers resolved from the old settings.

Even if that retry happens to work, cookie-export metadata is saved for the chosen browser while settings still name the old one. The next operation's saved-cookie check fails the browser/profile match and starts the recovery flow again. With multiple signed-in accounts, the stale account headers can also select the wrong channel or make otherwise valid replacement cookies fail.

**Evidence:**

- `BrowserLoginPromptForm.cs:72-113` presents all installed browsers as a selectable recovery choice.
- `SyncService.cs:674-704` exports from the selected browser and stores it only in the transient `_refreshedAuth`; it does not update or save application selection settings.
- `SyncService.cs:527-558` builds account-selection headers from the original `settings`, even when the supplied `auth` came from a different recovery browser.
- `CookieExportMetadataStore.cs:22-31` records the actual chosen browser/profile.
- `SyncService.cs:1163-1171` later accepts the saved export only when metadata matches the still-configured browser/profile.

**Suggested remediation:** Treat a cross-browser recovery choice as an explicit account-selection change: resolve and persist the matching browser account/profile and clear or re-resolve the YouTube account before retrying. Build headers from the effective recovered selection, not stale settings. If recovery must not change settings, remove alternate-browser choice and require recovery for the selected account only.

### YTS-021 - Cache-only account reads can indefinitely suppress live YouTube account refreshes

**Severity:** Medium  
**Category:** Account discovery correctness / cache policy  
**Status:** Confirmed by cache-mode and polling call-chain inspection

`DiscoverAccounts` uses one in-memory cache for both cache-only and network-enabled calls. An `allowNetwork: false` call merges persisted/local-scope accounts and stores them as a one-minute successful cache entry. A later `allowNetwork: true` call checks that same cache before considering its mode, returns the cache-only result, and never contacts YouTube.

Status/bootstrap construction performs cache-only discovery, and the web UI polls status repeatedly. When the entry expires, a status request can immediately repopulate it for another minute. The explicitly queued background network refresh can therefore keep receiving locally cached accounts forever. Newly added channels, renamed accounts, avatar/byline changes, or a changed active YouTube identity may not appear even though the UI says account refresh completed.

**Evidence:**

- `YouTubeAccountDiscoveryService.cs:54-62` returns any unexpired matching cache entry before checking `allowNetwork`.
- `YouTubeAccountDiscoveryService.cs:64-76` stores cache-only persisted/scope results with the full one-minute success lifetime.
- `LibraryWebServer.cs:466-480` performs `allowNetwork: false` account discovery while building every status/bootstrap payload.
- `TrayApplicationContext.cs:1882-1892` labels a later `allowNetwork: true` call as the background account refresh, but that call is still eligible for the cache-only early return.
- Existing account-discovery tests verify cache-only fallback data but do not verify that a subsequent network-enabled call bypasses a cache-only entry.

**Suggested remediation:** Tag cache entries with their provenance. Permit network-enabled calls to reuse only a successful live-network entry; cache-only results may serve read-only callers but must never satisfy an explicit live refresh. Alternatively, add a force-refresh API and use it for the queued account refresh. Add a regression test for `allowNetwork:false` followed immediately by `allowNetwork:true`.

### YTS-022 - The app reports automatic sync as armed or paused, but no automatic sync is scheduled

**Severity:** Medium  
**Category:** Sync state machine / misleading product behavior  
**Status:** Confirmed by complete sync-entry-point inspection

The persisted `AutoSyncArmed` flag and multiple user-facing messages say automatic sync is armed, satisfied, or paused until the user clicks Sync Now again. No timer, startup sync, file/network watcher, or retry scheduler ever consumes that armed state to start `RunSyncAsync`. If a manual sync leaves missing/failed targets, the flag remains armed but no retry occurs. If new items are added to Watch Later later, nothing downloads them automatically.

All ordinary sync entry points are explicit UI actions. The only `initiatedManually: false` call resumes a sync that was paused specifically because the user changed accounts; it is not a general background scheduler. The flag otherwise only gates a Watch Later *total* refresh and changes status text.

This is also conceptually inconsistent with the offline-mode design note that `Sync Now` should be explicit. Either manual-only behavior or automatic retry can be valid, but the current implementation exposes a nonfunctional automatic-sync state.

**Evidence:**

- `TrayApplicationContext.cs:97` and `TrayApplicationContext.cs:961-988` start sync from explicit tray/web Sync Now actions.
- `TrayApplicationContext.cs:1339-1355` is the sole non-manual start and only resumes a sync paused by an account-selection change.
- `TrayApplicationContext.cs:189-214` initializes local browsing/order/optimization but never starts or schedules sync.
- `TrayApplicationContext.cs:776-800` only persists the armed flag; it does not enqueue work.
- `TrayApplicationContext.cs:1742-1759` uses the flag only to permit a background Watch Later total refresh.
- `TrayApplicationContext.cs:520-526` and `TrayApplicationContext.cs:926-945` nevertheless describe automatic sync as armed or paused.
- `todo_harden_offline_mode.md:261-266` states that `Sync Now` should be explicit, underscoring the product-language conflict.

**Suggested remediation:** Choose and implement one clear model. For manual-only sync, remove `AutoSyncArmed` and all automatic-sync language; report only the last explicit sync outcome and remaining failures. For automatic retry, add a bounded scheduler with backoff, connectivity/busy checks, visible next-run state, cancellation, and tests showing that armed missing targets are actually retried.

### YTS-023 - Library-version polling never triggers an immediate video refresh

**Severity:** Low  
**Category:** UI refresh latency / polling logic  
**Status:** Confirmed by client state-update ordering inspection

The 1.5-second status poll is designed to reload `/api/videos` when `libraryVersion` changes. It calls `updateStatus` first, and that function immediately copies the response's version into `state.libraryVersion`. The subsequent comparison therefore compares the response value to itself and can never detect a change.

The separate 20-second age fallback eventually refreshes videos, so the defect is bounded rather than permanent. However, newly downloaded items, completed pending cards, watched/hidden changes from another tab, and other library updates can remain stale for up to 20 seconds despite the much faster status polling and explicit version field.

**Evidence:**

- `web-ui/app.js:1012-1029` calls `updateStatus` before evaluating `status.libraryVersion !== state.libraryVersion`.
- `web-ui/app.js:2208-2210` overwrites `state.libraryVersion` with that same status value.
- `web-ui/app.js:1025` provides only a 20-second fallback once the version comparison has been neutralized.
- No request-in-flight or previous-version variable preserves the pre-update value for comparison.

**Suggested remediation:** Capture the previous library version before calling `updateStatus`, or make `updateStatus` return whether it changed. Refresh videos immediately when the version advances and add a regression test that applies consecutive status payloads with different versions.

### YTS-024 - Phone-access status polling repeatedly launches full PowerShell processes

**Severity:** Low  
**Category:** Performance / phone-access implementation  
**Status:** Confirmed by client polling and server process-launch inspection

Every phone-access snapshot starts a new Windows PowerShell process to query WinRT hotspot state and Wi-Fi details. While Settings is open, the browser refreshes phone access about every five seconds indefinitely. A hotspot start/stop request then polls for up to 20 seconds at 500 ms intervals, and each polling iteration launches another complete PowerShell process.

One toggle can therefore create roughly forty sequential PowerShell processes in addition to the action process, and merely leaving Settings open creates continuing process churn. PowerShell startup/query time also stretches the nominal 20-second timeout because each synchronous query occurs inside the polling loop and has no timeout of its own.

**Evidence:**

- `web-ui/app.js:1018-1020` refreshes phone access from the 1.5-second status loop whenever its five-second age threshold is reached.
- `PhoneAccessProbe.cs:19-37` calls `HotspotInfo.TryRead` for every snapshot.
- `PhoneAccessProbe.cs:285-345` implements each read by calling `RunPowerShell`.
- `PhoneAccessProbe.cs:74-87` polls every 500 ms for up to 20 seconds by repeatedly calling `GetSnapshot`.
- `PhoneAccessProbe.cs:378-416` creates a fresh PowerShell process for every call and has no process timeout/cancellation.

**Suggested remediation:** Cache phone-access snapshots for a short interval and coalesce concurrent refreshes server-side. Use a single bounded asynchronous hotspot operation/polling session, or query the WinRT APIs directly in-process. At minimum, poll less frequently, add process timeouts/cancellation, and avoid launching another PowerShell instance when a recent snapshot is still valid.

## Verification notes

- Release test suite after the fixes on 2026-07-19: 97 passed, 1 explicitly skipped, 0 failed.
- Regression coverage verifies partial redownload backup restoration, preservation of cached order after an empty refresh, zero-target automatic-sync behavior, atomic JSON replacement and backup recovery, content-revision URL changes, single-snapshot parsing, and positive/negative authentication classification including ordinary `Sign in` titles.
- Debug test execution was blocked by the live `YouTubeSyncTray` process locking its Debug executable; the Release configuration was used without stopping the user's app.
