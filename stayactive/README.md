# StayActive

Windows tray app that toggles background activity with a left click.

- Left click the tray icon to turn StayActive on or off.
- When active, the app now explicitly asks Windows to keep both the system and display awake.
- Right click for options:
  - `Jiggle mouse`
  - `Type text`
  - `chrome-cursor-input-stay-awake`
  - `Edit text file`
  - `Open VM`
  - `Put Bluetooth on VM`
  - `Put Bluetooth on laptop`
- The tray icon shows an open eye when active and a closed eye when inactive.
- When `Jiggle mouse` is enabled, the app uses injected mouse movement instead of just relocating the cursor, which better resets Windows idle detection.
- When `Type text` is enabled, the app types the contents of `%LocalAppData%\StayActive\type-text.txt`, waits 5 seconds after reaching the end, and starts again.
- `chrome-cursor-input-stay-awake` controls Google Chrome only. While checked, extension version 1.5.0 targets exactly one supported RDP tab on a fresh random 20–35 second cadence and animates a bright, non-interactive 28 CSS-pixel magenta marker above the canvas: it moves 64 CSS pixels, holds for five seconds, and returns. When Chrome is in the background, the same bounded sequence sends exactly three `mouseMoved` events and one complete lowercase `f` keypress directly to the RDP tab; when Chrome has focus, it shows the marker as a visual-only preview and sends no remote input. It sends no clicks and never moves the Windows cursor or interrupts typing in another Windows app.
- The Chrome feature requires a one-time **Load unpacked** installation from `ChromeCursorInputStayAwakeExtension`. StayActive registers its per-user native messaging bridge only for Google Chrome. Chrome displays its standard debugger notification while the feature is enabled.
- `Open VM` and `Put Bluetooth on VM` use the experimental Docker/WSL USB handoff. The built-in MediaTek MT7925 has demonstrated an unsafe USB/IP reset failure on this laptop, so do not use those two actions until transport compatibility is resolved.
- `Put Bluetooth on laptop` detaches the adapter from WSL/Docker and verifies that the native Windows Bluetooth device is healthy. This recovery action remains available even if container setup is incomplete.
