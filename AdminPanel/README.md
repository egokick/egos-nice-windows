# Admin Panel

Standalone Nice Windows suite dashboard. It runs from the Windows notification area, can register itself to start with Windows, and opens the panel from its tray menu or double-click action.

Running the app also creates a **Nice Windows Admin Panel** shortcut in the current user's Windows Start Menu. The shortcut uses the dashboard's four-tile logo and opens the panel directly, including when the tray process is already running.

## Start behavior

Clicking **Start** in the Admin Panel always runs the selected app's `start.bat`; the panel never launches a previously built executable directly. This is intentional: each launcher is responsible for checking its source and applying an updated build before it starts the app.

- .NET apps run their normal incremental `dotnet build` or `dotnet publish`, so changed source is rebuilt and unchanged source is reused.
- Python apps run the source files directly after their runtime/dependency preparation, so they always use the current source.
- Browser-only apps open their current HTML source directly.
- Opticon checks its source tree against the installed command-center build and rebuilds/reinstalls when the source is newer.

If an update cannot be built or its required runtime cannot be prepared, Start fails with the launcher error instead of silently opening an older executable. To preserve this contract, add any new Admin Panel app with a `start.bat` that performs its own incremental build or otherwise runs the current source.

Run it with `start.bat`.
