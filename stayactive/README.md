# StayActive

Windows tray app that toggles background activity with a left click.

- Left click the tray icon to turn StayActive on or off.
- When active, the app now explicitly asks Windows to keep both the system and display awake.
- Right click for options:
  - `Jiggle mouse`
  - `Type text`
  - `Edit text file`
- The tray icon shows an open eye when active and a closed eye when inactive.
- When `Jiggle mouse` is enabled, the app uses injected mouse movement instead of just relocating the cursor, which better resets Windows idle detection.
- When `Type text` is enabled, the app types the contents of `%LocalAppData%\StayActive\type-text.txt`, waits 5 seconds after reaching the end, and starts again.
