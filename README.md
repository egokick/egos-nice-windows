# egos-nice-windows

Small monorepo for standalone Windows utilities.

## Apps

### YouTubeSyncTray

YouTube Watch Later sync tray app with:

- browser-backed local library UI
- local browser launch URL at `http://tom.localhost/` by default
- Chrome and Edge managed sign-in support
- local auth priming tool in `YouTubeSyncTray/AuthTool/`
- integration tests in `YouTubeSyncTray/IntegrationTests/`
- bundled sync tools in `YouTubeSyncTray/youtube-sync/`

Set `YOUTUBE_SYNC_BROWSER_HOST` to override the browser host if you want a different local name. If you use something other than `.localhost`, that hostname still needs to resolve to this machine through your `hosts` file or local DNS.

Run it from `YouTubeSyncTray/`.

### LightDarkToggle

Tray app that toggles:

- Windows app and system theme between light and dark
- Windows Terminal PowerShell profile color scheme between `Tango Light` and `One Half Dark`

Run it from `LightDarkToggle/`.

## Future Apps

Additional small Windows apps will live alongside `LightDarkToggle` as sibling directories in this repo.
