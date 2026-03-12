# LightDarkToggle

Minimal Windows tray app that toggles:

- Windows app theme and system theme between light and dark
- Windows Terminal PowerShell profile color scheme between `Tango Light` and `One Half Dark`

## Run

```powershell
dotnet run
```

Left-click the tray icon to toggle. Right-click for `Switch to ...` and `Exit`.

## Publish

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```
