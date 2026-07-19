# PowerModeToggle

A focused Windows tray app for the ASUS laptop power profiles already proven in `LightDarkToggle`.

- Left-click the tray icon to toggle between High and Low power.
- Right-click and enable **Auto Switch When Plugged In** to use High power on AC and Low power on battery.
- Right-click **Start With Windows** to enable or disable sign-in startup.
- The orange lightning icon means High power; the green leaf icon means Low power.

The app enables Windows startup on its first launch. It remembers later changes to both checkboxes in `%LOCALAPPDATA%\PowerModeToggle\settings.json`.

Power profile changes may request administrator approval once per app session. The elevated helper is then reused for later manual or automatic switches during that session.

Run `start.bat` to build the Release configuration and launch the tray app.
