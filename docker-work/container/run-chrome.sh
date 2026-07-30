#!/usr/bin/env bash
set -euo pipefail

export HOME=/home/chrome
export DISPLAY="${DISPLAY:-:99}"

until xdpyinfo -display "${DISPLAY}" >/dev/null 2>&1; do
    sleep 0.25
done

exec dbus-run-session -- google-chrome-stable \
    --user-data-dir=/home/chrome/work-profile \
    --remote-debugging-address=127.0.0.1 \
    --remote-debugging-port=9222 \
    --remote-allow-origins=http://127.0.0.1:9222 \
    --no-first-run \
    --no-default-browser-check \
    --start-maximized \
    --enable-logging=stderr \
    --vmodule='*/device/fido/*=2,*/device/bluetooth/*=2' \
    "${CHROME_START_URL}"
