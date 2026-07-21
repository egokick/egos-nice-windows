# Wi-Fi Devices

Small ASP.NET Core dashboard that polls Wi-Fi/LAN devices every five minutes, records MAC/IP/online history, and shows timelines, transition events, and day/hour online patterns. Finance now runs as its own app in `../finance`.

## Run

From this folder:

```powershell
dotnet run --urls http://127.0.0.1:5136
```

Then open:

```text
http://127.0.0.1:5136
```

## Configuration

The app reads `.env` from this folder by default. It supports either `KEY=VALUE` or `key:value` lines.

Recognized keys:

```text
url
username
password
devicecode
poll_interval_minutes
ping_timeout_ms
max_ping_hosts
ignore_tls_errors
```

The remote source tries basic auth, a few common login forms, JSON extraction, and HTML/table extraction. If the remote endpoint is unavailable or not recognizable, the app falls back to a Windows ARP/ping sweep on the local IPv4 subnet.

## Data

Runtime data stays local under `data/`:

```text
data/devices.json    device names and current state
data/history.jsonl   one sample per device per successful poll
data/events.jsonl    online/offline transitions
```

`.env`, `data/`, logs, and build output are ignored by git.
