# StayActive Remotes: self-hosted deployment scaffold

This is a **deployment template**, not a runnable demo. It creates an
operator-controlled Remotes control plane with:

- Headscale for coordination, using its embedded DERP/STUN and no public
  Tailscale DERP map;
- MeshCentral for consented screen/file-management sessions;
- RemoteHub for verified device-inventory policy and an append-only audit
  journal; and
- Caddy as the only TCP/TLS edge for the three operator-owned DNS names.

RemoteHub is an inventory and audit service, not a remote-command proxy. Its
same-origin `/admin/` page can manage approved mappings, but cannot start a
screen session, transfer a file, route traffic, run a command, or relay a
Bluetooth credential. Its Kestrel listener is not published to the host.

This scaffold does not use Tailscale's hosted control plane, public DERP map,
or a hosted remote-control service. The StayActive tray remains the end-user
GUI; MeshCentral and RemoteHub are centrally controlled operator consoles.

## Before deployment

Use a supported Linux host with a static public address, public DNS names,
valid TLS, encrypted backups, and an operator-owned OIDC provider. Open only
the deliberately selected edge bindings: TCP 80/443 for ACME and HTTPS, plus
UDP 3478 for embedded STUN. Never publish Headscale's administrative
gRPC/metrics listeners, MeshCentral, or RemoteHub directly.

Choose digest-pinned images after verifying publisher provenance. The
Headscale template includes a mandatory `grants: []` policy, which is deny-all
until an operator writes and reviews least-privilege grants. A missing
Headscale policy can otherwise allow more communication than intended.

## Required operator inputs

Copy `.env.example` to the ignored `.env`. Every blank value must be set before
Docker Compose can resolve the deployment. It intentionally contains **no**
password, API key, OIDC client secret, MeshCentral encryption key, RemoteHub
journal HMAC key, or bearer token.

| Variable group | Required values |
| --- | --- |
| Images and source | `CADDY_IMAGE`, `HEADSCALE_IMAGE`, `MESHCENTRAL_IMAGE`, `REMOTEHUB_SDK_IMAGE`, and `REMOTEHUB_RUNTIME_IMAGE` are vetted digest-pinned images. `REMOTEHUB_SOURCE_REVISION` is the full immutable revision of the checked-out RemoteHub source used to build the local image. |
| Public identity | `ACME_EMAIL`, `HEADSCALE_FQDN`, `MESHCENTRAL_FQDN`, `REMOTEHUB_FQDN`, and `HEADSCALE_PUBLIC_DERP_IPV4`. All DNS names and the DERP address must be owned by the operator. |
| Private Docker control network | `CONTROL_NETWORK_CIDR`, `CADDY_CONTROL_IP`, `HEADSCALE_CONTROL_IP`, `MESHCENTRAL_CONTROL_IP`, and `REMOTEHUB_CONTROL_IP`: a non-overlapping RFC1918 network with fixed addresses. Templates must trust only `CADDY_CONTROL_IP` as their reverse proxy. |
| Intentional host exposure | `EDGE_HTTP_BIND_ADDRESS`, `EDGE_HTTP_PUBLISHED_PORT`, `EDGE_HTTPS_BIND_ADDRESS`, `EDGE_HTTPS_PUBLISHED_PORT`, `DERP_STUN_BIND_ADDRESS`, and `DERP_STUN_PUBLISHED_PORT`. They start blank so the deployment fails instead of publishing a service by accident. |
| Rendered files and state | Absolute root-owned paths for Caddy, Headscale, MeshCentral, plus `REMOTEHUB_CONFIG_FILE` and `REMOTEHUB_JOURNAL_DIR`. |

The RemoteHub Compose build context is only `remote/RemoteHub`. It deliberately
does not send the parent deployment directory, its untracked `.env`, or
rendered secret configuration to the Docker builder. The Dockerfile accepts
only the two digest-pinned .NET base images and labels the resulting image with
`REMOTEHUB_SOURCE_REVISION` for traceability.

## Render production configuration

Render each `*.template` file to its ignored non-template sibling through a
root-controlled configuration renderer. Replace the existing Headscale and
MeshCentral `__REPLACE_*__` placeholders with the matching deployment values.
The Caddyfile is root-owned too; its `{$...}` values are expanded from the
non-secret Compose environment at startup.

For RemoteHub, render
[`config/remotehub/appsettings.Production.json.template`](config/remotehub/appsettings.Production.json.template)
to the root-owned path selected by `REMOTEHUB_CONFIG_FILE`. It must be mounted
read-only as `/app/appsettings.Production.json`. The renderer supplies:

- the operator-owned HTTPS OIDC issuer and RemoteHub token audience;
- a base64 journal HMAC key containing at least 32 random bytes, retrieved only
  from the secret manager;
- the operator-owned `REMOTEHUB_FQDN`; and
- the RemoteHub Admin public-client ID.

The sample keeps `JournalPath` fixed at
`/var/lib/stayactive-remotehub/inventory.journal.jsonl`, which maps to
`REMOTEHUB_JOURNAL_DIR`. Do not move the journal to SMB/NFS or share it among
writers. One RemoteHub instance owns one local persistent journal; corruption,
truncation, or a missing/incorrect HMAC key causes it to refuse startup.

The container is read-only, drops all Linux capabilities, and runs as numeric
UID/GID `65532`. On a rootful Docker host, create its journal directory with
that identity and keep the rendered configuration root-owned but readable by
that group, for example:

```sh
install -d -o 65532 -g 65532 -m 0700 /srv/stayactive-remotes/remotehub/journal
install -d -o root -g 65532 -m 0750 /etc/stayactive-remotes/remotehub
# Render from a root-only secret-manager workflow, then install it as:
install -o root -g 65532 -m 0640 /root/rendered/appsettings.Production.json \
  /etc/stayactive-remotes/remotehub/appsettings.Production.json
```

Use equivalent ownership/SELinux labels for rootless Docker or another runtime;
do not relax the rendered file to world-readable simply to make the mount work.
Back up the journal and its HMAC key separately and securely.

## OIDC registration: two separate public clients

RemoteHub validates issuer, audience, signature, expiry, scope (`scope` or
`scp`), and caller identity (`sub` or `client_id`). Configure the operator-owned
issuer to mint access tokens for the exact RemoteHub audience in the rendered
configuration and to include the named scopes below. Do not use a shared
client, a client secret, or an implicit/password/client-credentials grant for
either browser/native public client.

| Client | Required registration and scopes | Browser/CORS and token handling |
| --- | --- | --- |
| StayActive Windows tray | A **native public client** using Authorization Code plus mandatory `S256` PKCE. Allow its loopback callback on `http://127.0.0.1:<dynamic-port>/` according to the provider's native-app loopback support; do not substitute a LAN callback. Request exactly `openid profile remotehub.fleet.read offline_access`. | The tray exchanges the code locally and protects any refresh token with Windows DPAPI. It needs an access token with the RemoteHub audience and `remotehub.fleet.read`; it is not a browser origin, so browser CORS is not a replacement for the provider's native-public-client controls. |
| RemoteHub Admin page | A **browser public client** using Authorization Code plus mandatory `S256` PKCE, with no client secret. Register exactly `https://<REMOTEHUB_FQDN>/admin/` as the redirect URI and exactly `https://<REMOTEHUB_FQDN>` as its web origin. Request `openid profile remotehub.inventory.write remotehub.audit.read`; never grant or request `offline_access`. | Allow the exact Admin origin in the issuer's CORS policy for discovery/token requests. It needs an access token with the RemoteHub audience and the listed administrator scopes. The page keeps its access token only in memory and uses no refresh token. |

The `RemoteHub__AdminSpa__ClientId` in the rendered configuration is the second
client above. The Windows tray's issuer/client ID are configured in its Remotes
settings and are intentionally not copied into the central server's `.env`.
The server-side OIDC issuer used by RemoteHub must be HTTPS; its discovery,
authorization, token, and signing-key endpoints should be controlled by the
same operator. Restrict administrator scopes to a dedicated operator group at
the identity provider.

Headscale OIDC, if used, is a separate integration and may use a confidential
server-side client. See [`config/oidc/README.md`](config/oidc/README.md). Keep
that confidential secret in a mounted secret file rather than `.env`.

## Build, review, and start

1. Check out the exact reviewed source. Set `REMOTEHUB_SOURCE_REVISION` to the
   full `git rev-parse HEAD` value for that checkout and ensure the RemoteHub
   source has no local changes before building.
2. Copy `.env.example` to `.env`, fill every required non-secret value, and
   confirm no secret appears in it. Point `REMOTEHUB_CONFIG_FILE` to the
   root-owned rendered file above and `REMOTEHUB_JOURNAL_DIR` to its prepared
   persistent directory.
3. Review the fully resolved deployment before starting it:

   ```sh
   docker compose --env-file .env config
   docker compose --env-file .env build remotehub
   docker image inspect "stayactive-remotehub:<reviewed-source-revision>" \
     --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}'
   ```

   The inspected label must equal the reviewed source revision. Review the
   resolved Compose output to confirm that `remotehub` has no `ports:` entry
   and that only Caddy publishes TCP listeners.

4. After DNS, firewall, TLS, OIDC registration, and backup review, start the
   stack and validate the expected health endpoints:

   ```sh
   docker compose --env-file .env up -d
   docker compose --env-file .env exec headscale headscale configtest
   curl --fail --silent --show-error "https://remotehub.example.net/healthz"
   ```

   The Admin page is then available only at
   `https://remotehub.example.net/admin/`; complete its OIDC sign-in and verify it
   can create a test inventory mapping and show an audit record. Verify
   `https://${HEADSCALE_FQDN}/health`, inspect the client DERP map with
   `tailscale debug derp-map`, and confirm no public DERP regions are listed.
   On Windows clients, set `TS_NO_LOGS_NO_SUPPORT=true` before the first
   Headscale connection as defense-in-depth client log opt-out.

Do the initial MeshCentral administrator bootstrap locally and deliberately,
then retain `newAccounts: false`. There are no default accounts, passwords,
tokens, enrollment keys, or automatically trusted remote devices in this
scaffold.

## Safety and operations boundaries

- Enrollment, device visibility, screen viewing, exit-node routing, and file
  operations must remain individually authorized, auditable, and revocable.
  This infrastructure does not grant blanket remote access.
- Treat computer name, username, coarse location, screen content, and files as
  sensitive data. Collect location only by device-owner opt-in, not silent IP
  geolocation.
- Keep the Headscale policy deny-all until the operator has a reviewed
  capability/consent model. Do not use generic remote shell execution as an
  implementation shortcut.
- Taildrop is disabled so file transfer can be scoped, consented, hashed, and
  audited by the product layer.
- This scaffold explicitly does **not** include raw Bluetooth/HCI forwarding,
  Bluetooth passkey transport, credential relay, or virtual Bluetooth adapters.
  Use supported local-authenticator approval instead.

Useful references: [Headscale configuration](https://headscale.net/stable/ref/configuration/),
[Headscale embedded DERP](https://headscale.net/stable/ref/derp/),
[Headscale policy](https://headscale.net/stable/ref/policy/), and the
[MeshCentral configuration schema](https://github.com/Ylianst/MeshCentral/blob/master/meshcentral-config-schema.json).
