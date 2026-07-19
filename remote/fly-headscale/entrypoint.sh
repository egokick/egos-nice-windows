#!/bin/sh
set -eu

umask 077

: "${HEADSCALE_PUBLIC_IPV4:?HEADSCALE_PUBLIC_IPV4 is required}"

case "$HEADSCALE_PUBLIC_IPV4" in
  *[!0-9.]*|'')
    echo "HEADSCALE_PUBLIC_IPV4 is not an IPv4 address." >&2
    exit 1
    ;;
esac

mkdir -p /data/headscale /data/runtime /data/caddy /data/caddy-config /var/run/headscale

sed "s/__REPLACE_PUBLIC_DERP_IPV4__/$HEADSCALE_PUBLIC_IPV4/g" \
  /etc/headscale/config.yaml.template > "$HEADSCALE_CONFIG"
chmod 0600 "$HEADSCALE_CONFIG"

headscale configtest
caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile

headscale serve &
headscale_pid=$!
caddy run --config /etc/caddy/Caddyfile --adapter caddyfile &
caddy_pid=$!

shutdown() {
  trap - TERM INT
  kill -TERM "$caddy_pid" "$headscale_pid" 2>/dev/null || true
  wait "$caddy_pid" 2>/dev/null || true
  wait "$headscale_pid" 2>/dev/null || true
}

trap shutdown TERM INT

while kill -0 "$headscale_pid" 2>/dev/null && kill -0 "$caddy_pid" 2>/dev/null; do
  sleep 1
done

echo "Headscale or Caddy exited unexpectedly; stopping the Fly Machine." >&2
shutdown
exit 1
