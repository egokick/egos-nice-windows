# LightDarkToggle

Minimal Windows tray app that toggles:

- Windows app theme and system theme between light and dark
- Windows Terminal PowerShell profile color scheme between `Tango Light` and `One Half Dark`
- Windows startup for the app itself via a tray toggle
- Automatic timed switching between light and dark mode

## Run

```powershell
dotnet run
```

Left-click the tray icon to toggle immediately.

Right-click the tray icon for:

- `Switch to ...`
- `Run at Windows startup`
- `Timed Light/Dark`
- `Exit`

When `Timed Light/Dark` is enabled, the right-click tray menu expands with:

- `Light`: the hour when light mode begins
- `Dark`: the hour when dark mode begins

The app saves that schedule and checks once per minute whether the current system time should be light or dark mode.

## Publish

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```
