# LightDarkToggle

Minimal Windows tray app that toggles:

- Windows app theme and system theme between light and dark
- Windows Terminal PowerShell profile color scheme between `Tango Light` and `One Half Dark`
- Windows startup for the app itself via a tray toggle
- Automatic timed switching between light and dark mode
- Monitor brightness from the tray menu when the display exposes brightness control
- Software dimming below the display's hardware minimum

## Run

```powershell
dotnet run
```

Left-click the tray icon to toggle immediately.

Right-click the tray icon for:

- `Switch to ...`
- `Admin Panel`
- `Run at Windows startup`
- `Brightness` slider
- `Extra dimming` slider
- `Timed Light/Dark`
- `Exit`

## Admin Panel

Choose `Admin Panel` from the tray menu to open the centered suite dashboard. It contains cards for Parakeet Mic, Power Mode Toggle, Stay Active, Voice Codex, Wi-Fi Devices, Workflow Manager, YouTube Sync Tray, Light / Dark Toggle, Nemotron Mic, and Ollama Coder Agent.

Each card includes:

- The app's logo, name, and description
- A large `Launch` button that prepares the app's required runtime first (installing missing .NET, Python, Ollama, or YouTube tools when needed), then runs its existing `start.bat`
- A `Start with Windows` toggle stored in the current user's Windows Run settings
- A drag handle for reordering cards; `Ctrl` + arrow keys provide a keyboard alternative

The content-only grid shows at most three cards per row, placing further cards on subsequent rows. The card order is saved in `%LOCALAPPDATA%\LightDarkToggle\admin-panel.json`. Only one Admin Panel window is opened at a time, and it follows the current Windows light or dark theme.

When `Timed Light/Dark` is enabled, the right-click tray menu expands with:

- `Light`: the hour when light mode begins
- `Dark`: the hour when dark mode begins

The app saves that schedule and checks once per minute whether the current system time should be light or dark mode.

Brightness control uses DDC/CI for external monitors when available and falls back to WMI for built-in panels. If Windows cannot control brightness for the current display, the slider shows as unavailable.

Extra dimming places a black, click-through compositor overlay over each active display after hardware brightness has reached its minimum. The overlay does not take focus or intercept mouse and keyboard input, works independently of HDR and display-driver gamma-ramp limits, and is removed when extra dimming is turned off or the app exits.

## Publish

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```
