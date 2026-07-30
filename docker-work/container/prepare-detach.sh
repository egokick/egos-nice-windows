#!/usr/bin/env bash
set -euo pipefail

supervisorctl=(supervisorctl -s unix:///run/stayactive/supervisor.sock)

if command -v bluetoothctl >/dev/null 2>&1; then
    timeout 5 bluetoothctl power off >/dev/null 2>&1 || true
fi
timeout 5 btmgmt power off >/dev/null 2>&1 || true

timeout 10 "${supervisorctl[@]}" stop bluetoothd >/dev/null 2>&1 || true

deadline=$((SECONDS + 10))
while pgrep -x bluetoothd >/dev/null 2>&1 && ((SECONDS < deadline)); do
    sleep 0.25
done

if pgrep -x bluetoothd >/dev/null 2>&1; then
    echo "bluetoothd did not stop cleanly." >&2
    exit 1
fi

if timeout 5 btmgmt info 2>/dev/null | grep -qi 'current settings:.*powered'; then
    echo "hci0 remained powered after the bounded shutdown." >&2
    exit 1
fi

echo "STAYACTIVE_BLUETOOTH_READY_TO_DETACH"
