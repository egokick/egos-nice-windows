#!/usr/bin/env bash
set -euo pipefail

install -d -m 0755 /run/dbus /run/stayactive /var/log/stayactive
rm -f /run/dbus/pid /run/dbus/system_bus_socket
install -d -o chrome -g chrome -m 0700 /home/chrome/work-profile
install -d -o chrome -g chrome -m 0750 /home/chrome/Downloads
chown chrome:chrome /home/chrome/work-profile /home/chrome/Downloads

exec /usr/bin/supervisord -n -c /etc/supervisor/supervisord.conf
