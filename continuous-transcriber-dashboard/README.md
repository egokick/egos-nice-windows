# Continuous Transcriber Dashboard

Standalone Windows dashboard for browsing and synchronizing Continuous Transcriber archives across Opticon devices.

Run `start.bat`. The app reads `..\Continuous-transcriber` directly, serves only on `http://127.0.0.1:5138`, and keeps downloaded device archives under `%LOCALAPPDATA%\ContinuousTranscriberDashboard\devices\<immutable-device-id>`.

Remote access is denied unless this machine's enrolled Opticon Agent role is `ControllerAndManaged`. Remote archive folders must be configured as guarded Opticon shared roots. The UI uses Opticon's immutable device ID internally and displays the configured machine name.

Useful CLI commands:

```text
opticon transcriptions devices --json
opticon transcriptions sync --device <id> --destination <folder> --start <ISO-8601> --end <ISO-8601> --json
```

`--metadata-only` downloads overlapping transcript text without audio. `--move` conditionally deletes each origin file only after the local copy has the same SHA-256 digest and length.