#!/usr/bin/env bash
set -euo pipefail

mode="${1:-full}"
failures=()
exact_usb_attached=0

check() {
    local description="$1"
    shift
    if ! "$@" >/dev/null 2>&1; then
        failures+=("${description}")
    fi
}

check "system D-Bus" test -S /run/dbus/system_bus_socket
check "system D-Bus roundtrip" timeout 5 busctl --system list
check "X display" timeout 5 xdpyinfo -display "${DISPLAY:-:99}"
check "x11vnc listener" nc -z -w 3 127.0.0.1 5900
check "noVNC" curl --fail --silent --max-time 3 http://127.0.0.1:6080/vnc.html
check "passkey test server" curl --fail --silent --max-time 3 http://127.0.0.1:8000/
check "Chrome DevTools" curl --fail --silent --max-time 3 http://127.0.0.1:9222/json/version
check "page-scoped keepalive" pgrep -f /opt/stayactive/stayactive-keepalive.py

# The Docker HEALTHCHECK may stay container-only while Bluetooth is deliberately
# on Windows. As soon as the exact USB device appears in WSL's sysfs, however,
# it must enforce the complete radio/BlueZ contract instead of reporting a
# false healthy state for an attached-but-broken adapter.
for vendor_file in /sys/bus/usb/devices/*/idVendor; do
    [[ -r "${vendor_file}" ]] || continue
    device_dir="$(dirname "${vendor_file}")"
    if grep -qix '13d3' "${vendor_file}" \
        && [[ -r "${device_dir}/idProduct" ]] \
        && grep -qix '3602' "${device_dir}/idProduct"; then
        exact_usb_attached=1
        break
    fi
done

if [[ "${mode}" != "--base" ]] \
    && [[ "${mode}" != "--container" || "${exact_usb_attached}" = "1" ]]; then
    check "hci0" test -e /sys/class/bluetooth/hci0
    check "org.bluez" timeout 5 busctl --system status org.bluez
    check "BlueZ adapter" timeout 5 bluetoothctl show

    bluetoothd_count="$(pgrep -xc bluetoothd || true)"
    if [[ "${bluetoothd_count}" != "1" ]]; then
        failures+=("exactly one bluetoothd")
    fi

    usb_identity_ok=0
    device_path="$(readlink -f /sys/class/bluetooth/hci0/device 2>/dev/null || true)"
    while [[ -n "${device_path}" && "${device_path}" != "/" ]]; do
        if [[ -r "${device_path}/idVendor" && -r "${device_path}/idProduct" ]] \
            && grep -qix '13d3' "${device_path}/idVendor" \
            && grep -qix '3602' "${device_path}/idProduct"; then
            usb_identity_ok=1
            break
        fi
        device_path="$(dirname "${device_path}")"
    done
    if [[ "${usb_identity_ok}" != "1" ]]; then
        failures+=("hci0 exact USB identity 13d3:3602")
    fi

    if timeout 5 bluetoothctl show >/tmp/stayactive-bluetooth-show.txt 2>/dev/null; then
        grep -q 'Powered: yes' /tmp/stayactive-bluetooth-show.txt \
            || failures+=("Bluetooth powered")
    fi

    if timeout 5 btmgmt info >/tmp/stayactive-btmgmt-info.txt 2>/dev/null; then
        grep -qi '\<le\>' /tmp/stayactive-btmgmt-info.txt \
            || failures+=("Bluetooth LE support")
    else
        failures+=("btmgmt info")
    fi

    check "Chrome hybrid transport" \
        timeout 35 python3 /opt/stayactive/check-chrome-hybrid.py 25
fi

if ((${#failures[@]} > 0)); then
    printf 'UNHEALTHY: %s\n' "$(IFS=', '; echo "${failures[*]}")" >&2
    exit 1
fi

echo "STAYACTIVE_DOCKER_WORK_HEALTHY"
