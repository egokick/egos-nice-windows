#!/usr/bin/env bash
set -euo pipefail

supervisorctl=(supervisorctl -s unix:///run/stayactive/supervisor.sock)

deadline=$((SECONDS + 30))
while [[ ! -e /sys/class/bluetooth/hci0 ]] && ((SECONDS < deadline)); do
    sleep 1
done

if [[ ! -e /sys/class/bluetooth/hci0 ]]; then
    echo "hci0 did not appear." >&2
    exit 1
fi

supervisor_deadline=$((SECONDS + 30))
while true; do
    supervisor_state="$(
        timeout 5 "${supervisorctl[@]}" status bluetoothd 2>/dev/null \
            | awk '{print $2}' \
            || true
    )"
    case "${supervisor_state}" in
        RUNNING)
            break
            ;;
        STARTING)
            ;;
        STOPPED|EXITED|FATAL|BACKOFF|"")
            timeout 10 "${supervisorctl[@]}" start bluetoothd >/dev/null 2>&1 || true
            ;;
        *)
            echo "Unexpected bluetoothd supervisor state: ${supervisor_state}" >&2
            exit 1
            ;;
    esac

    if ((SECONDS >= supervisor_deadline)); then
        echo "bluetoothd did not reach RUNNING under Supervisor." >&2
        exit 1
    fi
    sleep 0.5
done

wait_for_adapter() {
    local deadline=$((SECONDS + 30))
    until timeout 5 bluetoothctl show >/dev/null 2>&1; do
        if ((SECONDS >= deadline)); then
            return 1
        fi
        sleep 1
    done
}

if ! wait_for_adapter; then
    timeout 15 "${supervisorctl[@]}" restart bluetoothd >/dev/null
    if ! wait_for_adapter; then
        echo "BlueZ did not expose the Bluetooth adapter after one bounded restart." >&2
        exit 1
    fi
fi

timeout 5 bluetoothctl power on >/dev/null
/opt/stayactive/healthcheck.sh
