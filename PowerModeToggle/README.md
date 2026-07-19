# PowerModeToggle

One Windows tray app that detects the current machine and selects its matching power-profile backend. It deliberately refuses to change power settings on unrecognized hardware.

Supported machines:

- The ASUS laptop: uses the existing Armoury Crate/ASUS firmware, Windows plan, Windows power-mode, and 60/120 Hz controls.
- The GIGABYTE Z790 EAGLE AX desktop with an Intel Core i9-14900K: uses the desktop Windows plans, CPU policy, RTX 4090 power limit, DDC/CI brightness, and 60/165 Hz controls.

The desktop profiles are:

| Setting | Low power | High power |
| --- | --- | --- |
| Windows plan | `PowerModeToggleDesktop Low` | `PowerModeToggleDesktop High` |
| CPU minimum / maximum state | 5% / 80% | 5% / 100% |
| CPU energy preference | 95% efficiency | 5% efficiency |
| Hybrid CPU scheduling | Prefer efficient cores | Automatic |
| CPU boost | Disabled | Aggressive |
| RTX 4090 power limit | 150 W | 450 W |
| Primary display | 60 Hz | 165 Hz |
| Monitor brightness | 35% | 100% |

Left-click the tray icon to toggle. The right-click menu contains status, **Auto Switch When Plugged In**, **Start With Windows**, and **Exit**. The orange lightning icon means High power; the green leaf means Low power.

The app preserves the laptop settings in `%LOCALAPPDATA%\PowerModeToggle` and the desktop settings in `%LOCALAPPDATA%\PowerModeToggleDesktop`. On the desktop, it also migrates the old `PowerModeToggleDesktop` startup entry to the unified executable.

Power changes may request administrator approval once per app session. The elevated helper is reused for subsequent switches.

Run `start.bat` to publish a self-contained, single-file x64 Release build and launch the unified app. Diagnostic commands:

```text
PowerModeToggle.exe --probe-machine-profile <output.json>
PowerModeToggle.exe --probe-power-state <output.json>
PowerModeToggle.exe --apply-power-profile LowPower|HighPower <output.json>
```
