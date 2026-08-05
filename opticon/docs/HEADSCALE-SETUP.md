# Opticon with self-hosted Headscale

Opticon uses the standard Windows Tailscale client only as a WireGuard mesh client. Its control plane is the operator's Headscale 0.29.3 service on Fly; Opticon makes no calls to `api.tailscale.com` and does not use Tailscale OAuth.

The public Fly gateway exposes only required Tailscale protocol/DERP routes, health, and the exact pinned installer mirror. Raw `/api`, helper, Swagger, registration, and authentication pages are not public. Opticon administration uses the path `/opticon/v1/headscale/`, an exact method/path allowlist, timestamped HMAC-SHA256 requests, and one-use nonce replay protection. The actual Headscale bearer is a Fly secret and never leaves the Fly process boundary. The independent HMAC secret is protected by Windows DPAPI on this laptop.

Headscale `node.expiry` is zero and invitations create durable tagged nodes, so a powered-off machine does not expire merely because it is offline for 91 days. Its WireGuard/Headscale node identity is independent of the administrative HMAC secret and of the laptop's current Wi-Fi/public address.

Normal deployment from this laptop takes an encrypted-volume snapshot and runs `flyctl deploy --remote-only --app taildesk-egokick-control --yes` from `fly-headscale`. Do not recreate the app, volume, or dedicated IPs during a normal deployment. Rotate `HEADSCALE_API_KEY` only inside Fly, and rotate `OPTICON_ADMIN_HMAC_KEY` together with the DPAPI-protected command-center value.