# StayActive

Windows tray app that toggles background activity with a left click.

- Left click the tray icon to turn StayActive on or off.
- When active, the app now explicitly asks Windows to keep both the system and display awake.
- Right click for options:
  - `Jiggle mouse`
  - `Type text`
  - `Edit text file`
  - `Open VM`
  - `Put Bluetooth on VM`
  - `Put Bluetooth on laptop`
- The tray icon shows an open eye when active and a closed eye when inactive.
- When `Jiggle mouse` is enabled, the app uses injected mouse movement instead of just relocating the cursor, which better resets Windows idle detection.
- When `Type text` is enabled, the app types the contents of `%LocalAppData%\StayActive\type-text.txt`, waits 5 seconds after reaching the end, and starts again.
- `Open VM` starts the `WorkRDP` VirtualBox VM with the OS disk first in the boot order.
- `Put Bluetooth on VM` resets the laptop MediaTek Bluetooth adapter, clears stale VirtualBox USB capture state if needed, starts `WorkRDP`, and verifies the MediaTek USB Bluetooth adapter is attached to the VM. This requires an administrator prompt because Windows must release the host Bluetooth driver.
- `Put Bluetooth on laptop` detaches the MediaTek Bluetooth USB adapter from the VM, disables the VM USB filter, and gives the adapter back to host Windows.
