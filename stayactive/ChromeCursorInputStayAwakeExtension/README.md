# chrome-cursor-input-stay-awake

This Manifest V3 extension lets StayActive send a clearly visible, bounded
mouse movement directly to a supported remote-session page in **Google
Chrome**. It does not move the Windows cursor or send clicks. While the remote
tab is in the background, every pulse also sends one lowercase `f` keypress
directly to that tab.

The current extension version is `1.5.0`.

The fixed extension ID is:

`ipkfokmlojonbenedebhbkkdnafgdpfh`

## Install in Google Chrome

1. Install or start StayActive so it can register the native messaging host
   `com.stayactive.chrome_cursor_input`.
2. Open `chrome://extensions` in Google Chrome. Do not use Microsoft Edge.
3. Turn on **Developer mode**.
4. Click **Load unpacked** and select this
   `ChromeCursorInputStayAwakeExtension` directory.
5. Confirm the displayed extension ID is
   `ipkfokmlojonbenedebhbkkdnafgdpfh`.
6. Use the StayActive option named `chrome-cursor-input-stay-awake`.

Chrome displays a debugger notification while the feature is enabled. That is
expected: Chrome requires the debugger interface for page-directed mouse
movement. If you cancel that notification, the extension will not attach again
until the StayActive option is turned off and back on.

After updating an already loaded unpacked extension, click **Reload** on its
card at `chrome://extensions` and confirm the version reads `1.5.0`.

## Safety behavior

- Only exact HTTPS hosts are eligible:
  `windows.cloud.microsoft`, `windows365.microsoft.com`,
  `rdweb.wvd.microsoft.com`, and `client.wvd.microsoft.com`.
- Exactly one eligible tab must be open. With zero or multiple eligible tabs,
  the extension sends no input.
- Input is skipped while the eligible tab is active in the focused Chrome
  window, so real use is not interrupted. In that foreground state, the
  extension instead animates only its non-interactive diagnostic marker and
  reports whether Chrome could display it. Before every mouse event and key
  down, the extension refreshes the tab's current URL, loading state, active
  state, and window focus; disabling, navigating, closing, or foregrounding
  the target stops subsequent input in the current pulse.
- The debugger detaches when the feature is disabled, the native host cannot
  confirm state, the tab navigates, or the eligible target becomes ambiguous.
- A pulse runs immediately when the feature attaches. After that, a fresh
  random delay from 20 through 35 seconds (inclusive) is chosen for every
  pulse. The next delay begins when a pulse starts, so the marker animation
  does not lengthen that cadence. A one-shot Chrome alarm provides a
  duplicate-safe fallback if the normal in-worker timer is interrupted. Each
  pulse sends three `mouseMoved` events inside the selected canvas, iframe,
  application surface, or viewport fallback, plus one complete lowercase `f`
  key-down/key-up pair directly to the same background tab. It establishes a
  safe point, waits 150 ms, moves 64 CSS pixels when the surface has room,
  sends `f`, holds that position for 5,000 ms so the remote cursor is visible,
  and returns to the safe point. The key-up is attempted even if key-down
  reports an error, and one failed key-up is retried immediately, reducing the
  risk of a stuck remote key.
- A bright 28 CSS-pixel diamond is drawn at the same three positions using
  Chrome's compositor-level DevTools overlay. It is always above page content,
  cannot receive mouse or keyboard input, does not inspect or modify page
  content, briefly remains at the returned safe point for 250 ms, and is
  removed after every pulse. Overlay drawing is best-effort:
  if Chrome cannot display it, the safe `mouseMoved` pulse still proceeds.
  Chrome's legacy protocol rectangle is used if the primary quad overlay is
  unavailable.
- On a surface smaller than 64 CSS pixels, the movement is shortened to the
  available width or height. Both positions remain inside that exact clipped
  surface and inside the viewport.
- Status messages sent to StayActive contain fixed status codes only. They do
  not contain URLs, tab IDs, titles, page content, or browsing history.

## Developer check

Run `node --test background.test.cjs` from this directory. The self-test covers
the randomized timing endpoints and races, durable fallback recovery,
background mouse and lowercase `f` pulse, foreground visual-only preview,
key-release handling, mid-pulse focus/navigation/disable transitions, overlay
fallback and cleanup, exact-host filtering, multiple-tab fail-closed behavior,
disable/detach, and user-cancel handling.
