# LightDarkToggle

Minimal Windows tray app that toggles:

- Windows app theme and system theme between light and dark
- Windows Terminal PowerShell profile color scheme between `Tango Light` and `One Half Dark`
- Windows startup for the app itself via a tray toggle
- Automatic timed switching between light and dark mode
- Monitor brightness from the tray menu when the display exposes brightness control

## Run

```powershell
dotnet run
```

Left-click the tray icon to toggle immediately.

Right-click the tray icon for:

- `Switch to ...`
- `Run at Windows startup`
- `Brightness` slider
- `Timed Light/Dark`
- `Exit`

When `Timed Light/Dark` is enabled, the right-click tray menu expands with:

- `Light`: the hour when light mode begins
- `Dark`: the hour when dark mode begins

The app saves that schedule and checks once per minute whether the current system time should be light or dark mode.

Brightness control uses DDC/CI for external monitors when available and falls back to WMI for built-in panels. If Windows cannot control brightness for the current display, the slider shows as unavailable.

## Publish

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```
