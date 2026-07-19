# Start launcher dependency bootstraps

The repository's `start.bat` launchers call these shared, idempotent helpers before they build or run an app.

- `ensure-dotnet-sdk.bat` installs a missing SDK per-user with Microsoft's `dotnet-install.ps1`.
- `ensure-python.ps1` installs checksum-verified Python 3.12.10 per-user, creates an app-local `.venv`, and installs `requirements.txt` only when its hash changes or `pip check` fails.
- `ensure-youtube-sync-tools.ps1` installs checksum-verified yt-dlp and FFmpeg assets in YouTubeSyncTray's expected bundle layout.
- `ensure-docker-desktop.ps1` and `ensure-winget-package.ps1` install missing Winget packages and verify that their executables are available.
- `ensure-ollama-coder.ps1` starts Ollama and installs the `qwen3-coder:30b` model behind the local `coder-files` alias when needed. The model download is approximately 19 GB.

The WorkVM launcher reuses its existing VirtualBox installer, which requests administrator approval only when VirtualBox is absent.
