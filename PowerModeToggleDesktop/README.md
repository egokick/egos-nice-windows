# PowerModeToggleDesktop

A desktop-specific variant of `PowerModeToggle` for this PC:

- GIGABYTE Z790 EAGLE AX
- Intel Core i9-14900K
- NVIDIA GeForce RTX 4090
- AOC 2560x1440 165 Hz monitor with DDC/CI brightness control

Left-click the tray icon to toggle between the two verified profiles:

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

The app creates its two Windows plans from Balanced the first time they are needed. It does not modify the built-in plans.

The tray behavior matches the laptop app:

- **Auto Switch When Plugged In** remains available. This desktop always reports AC power, so enabling it keeps High power selected.
- **Start With Windows** controls sign-in startup and is enabled on first launch.
- The orange lightning icon means High power; the green leaf means Low power.

Profile changes request administrator approval once per app session because changing the NVIDIA power ceiling requires elevation. The helper is reused for subsequent switches.

Settings are stored separately in `%LOCALAPPDATA%\PowerModeToggleDesktop\settings.json`.

Run `start.bat` to build the Release configuration and launch the tray app.

For a read-only hardware check, run `PowerModeToggleDesktop.exe --probe-power-state <output.json>`.

For an end-to-end profile check (including the same elevated broker used by the tray), run
`PowerModeToggleDesktop.exe --apply-power-profile LowPower|HighPower <output.json>`.
