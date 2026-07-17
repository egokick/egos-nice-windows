# StayActive Remotes: self-hosted deployment scaffold

This is a **deployment template**, not a runnable demo. It creates an
operator-controlled Remotes control plane with:

- Headscale for coordination, using its embedded DERP/STUN and no public
  Tailscale DERP map;
- MeshCentral for consented screen/file-management sessions;
- RemoteHub for verified device-inventory policy and an append-only audit
  journal;
- a Windows Enrollment Controller, isolated from RemoteHub and Docker, for
  one-time Headscale enrollment tickets; and
- Caddy as the only TCP/TLS edge for the operator-owned DNS names.

RemoteHub is an inventory and audit service, not a remote-command proxy. Its
same-origin `/admin/` page can manage approved mappings, but cannot start a
screen session, transfer a file, route traffic, run a command, or relay a
Bluetooth credential. Its Kestrel listener is not published to the host.

This scaffold does not use Tailscale's hosted control plane, public DERP map,
or a hosted remote-control service. The StayActive tray remains the end-user
GUI; MeshCentral and RemoteHub are centrally controlled operator consoles.

## Before deployment

Use an operator-controlled Windows Docker host with public DNS/TLS, encrypted
backups, and an operator-owned OIDC provider. The Windows enrollment controller
must run on that same host: Caddy exposes its Headscale administrative route on
host loopback only, deliberately preventing a remote controller from reaching
it. The controller runs under a non-administrator service identity and is the
only workload permitted to hold the Headscale management key. Open only the
deliberately selected public edge bindings: TCP 80/443 for ACME and HTTPS, plus
UDP 3478 for embedded STUN. Never publish Headscale's administrative
gRPC/metrics listeners, MeshCentral, RemoteHub, or the controller directly.

This is the supported current topology. A future Fly deployment requires a
distinct, separately reviewed controller/credential architecture; do not assume
a Windows Credential Manager service can run there unchanged or relax the
controller-only loopback boundary as a workaround.

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
| Images and source | `CADDY_IMAGE`, `HEADSCALE_IMAGE`, `MESHCENTRAL_IMAGE`, `REMOTEHUB_SDK_IMAGE`, and `REMOTEHUB_RUNTIME_IMAGE` are vetted digest-pinned images. `REMOTEHUB_SOURCE_REVISION` identifies the immutable reviewed source for the Docker image. Publish the Windows controller from the same reviewed source separately; it is not a Docker image. |
| Public identity | `ACME_EMAIL`, `HEADSCALE_FQDN`, `MESHCENTRAL_FQDN`, `REMOTEHUB_FQDN`, and `HEADSCALE_PUBLIC_DERP_IPV4`. All DNS names and the DERP address must be owned by the operator. |
| Private control path | `CONTROL_NETWORK_CIDR`, `CADDY_CONTROL_IP`, `HEADSCALE_CONTROL_IP`, `MESHCENTRAL_CONTROL_IP`, `REMOTEHUB_CONTROL_IP`, and `WINDOWS_ENROLLMENT_CONTROLLER_IP`. The last value is the private controller address reachable only by Caddy for ticket forwarding; restrict its Windows firewall rule to the fixed Caddy peer and port 5091. |
| Intentional host exposure | `EDGE_HTTP_BIND_ADDRESS`, `EDGE_HTTP_PUBLISHED_PORT`, `EDGE_HTTPS_BIND_ADDRESS`, `EDGE_HTTPS_PUBLISHED_PORT`, `DERP_STUN_BIND_ADDRESS`, and `DERP_STUN_PUBLISHED_PORT`. They start blank so the deployment fails instead of publishing a service by accident. Caddy's controller-only Headscale API listener is fixed to host loopback on TCP 4443; it is not a LAN or public port. |
| Rendered files and state | Absolute root-owned paths for Caddy, Headscale, MeshCentral, and RemoteHub state. The Windows controller owns its own ACL-protected configuration and journal state. There is no Docker enrollment-controller service, Compose API-key variable, or Headscale API-key file path. |

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

## Windows enrollment controller and credential boundary

Enrollment-ticket issuance is performed by a dedicated **Windows service**,
not by a Docker workload. Caddy forwards only
`/api/v1/enrollment-tickets` traffic from the RemoteHub public origin to the
controller's private port 5091. RemoteHub remains inventory/audit only and no
container receives a Headscale management credential.

The controller uses the separate, controller-only Headscale API origin
`https://headscale-controller.stayactive.test:4443/`. Caddy publishes that
listener on host loopback only and serves `/api/v1/*` there; the ordinary
Headscale name returns `404` for that path. Install the matching loopback hosts
entry and the Caddy local CA only on the Windows controller host. Do not put the
controller-only name in LAN/public DNS and do not open TCP 4443 in a firewall.

Create one long-lived Headscale controller API key only after the Headscale
owner and policy are ready. The provisioning process must run as the dedicated
controller service identity and write the raw key once to that identity's
Windows Credential Manager target:

```text
StayActive/HeadscaleController/v1
```

The provisioning binary accepts the raw value only on standard input through
`--store-controller-key`; it must never receive the value on a command line,
write it to a file, print it, place it in `.env`, mount it into Docker, or copy
it into a rendered Caddy/RemoteHub configuration. Record only non-secret key
metadata needed for revocation. Do not create a file-mounted controller key or
an enrollment-controller Docker service.

The service must use a dedicated non-administrator local account, retain a
service SID, bind its ticket API only to the reviewed private/virtual interface,
and have a Windows firewall rule that permits TCP 5091 only from Caddy's fixed
control-network peer. Its durable ticket journal, published service files, and
integrity material belong under the ACL-protected
`%ProgramData%\StayActiveRemotes\EnrollmentController` root, not in a
user-writable checkout or `remote/lan-test/state`. Back up that protected state
without ever exporting the Credential Manager API key.

Ticket policy is fixed: a fresh owner authorization may issue exactly one
non-reusable Headscale pre-authentication key for **15 minutes**. The controller
fixes the device/exit-node tags, persists only safe ticket metadata and status,
and returns the raw enrollment key only in the initial response. Revoke it on
failed persistence or an explicit owner revocation; never return it from
status, audit, or retry paths.

## OIDC registration: three separate public clients

RemoteHub and the Windows enrollment controller validate issuer, audience,
signature, expiry, scope (`scope` or `scp`), and caller identity (`sub` or
`client_id`). Configure
the operator-owned issuer to mint access tokens only for the exact audience and
scopes listed below. Tokens that call the Admin
inventory or audit APIs must additionally carry the dedicated flat `role` claim
`stayactive.remotehub.admin`; assign it only to approved operators. Do not use
a shared client, a client secret, or an implicit/password/client-credentials
grant for any browser/native public client.

| Client | Required registration and scopes | Browser/CORS and token handling |
| --- | --- | --- |
| StayActive Windows tray | A **native public client** using Authorization Code plus mandatory `S256` PKCE. Allow its loopback callback on `http://127.0.0.1:<dynamic-port>/` according to the provider's native-app loopback support; do not substitute a LAN callback. For Keycloak, register the exact root form `http://127.0.0.1/` (including the trailing slash), which permits the dynamic port but rejects child paths. Request exactly `openid profile remotehub.fleet.read offline_access`. | The tray exchanges the code locally and protects any refresh token with Windows DPAPI. It needs an access token with the RemoteHub audience and `remotehub.fleet.read`; it is not a browser origin, so browser CORS is not a replacement for the provider's native-public-client controls. |
| RemoteHub Admin page | A **browser public client** using Authorization Code plus mandatory `S256` PKCE, with no client secret. Register exactly `https://<REMOTEHUB_FQDN>/admin/` as the redirect URI and exactly `https://<REMOTEHUB_FQDN>` as its web origin. Request `openid profile remotehub.inventory.write remotehub.audit.read remotehub.admin`; never grant or request `offline_access`. | Allow the exact Admin origin in the issuer's CORS policy for discovery/token requests. It needs an access token with the RemoteHub audience and the listed administrator scopes. The page keeps its access token only in memory and uses no refresh token. |
| Remotes enrollment | A third **native public client**, `stayactive-remotes-enrollment`, using Authorization Code plus mandatory `S256` PKCE and the loopback root `http://127.0.0.1/`. Request exactly `openid profile stayactive.enrollment.write`; no refresh token and no `offline_access`. Mint the `stayactive-enrollment` audience and a flat `role` claim containing only `stayactive.enrollment.admin` for approved owners. | The tray must make this a fresh interactive authorization when the owner chooses **Add device**. The Windows controller requires both the dedicated scope and role, fixes each ticket to one use and exactly 15 minutes, and never persists the raw Headscale pre-authentication key. |

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
   full `git rev-parse HEAD` value and ensure the RemoteHub source tree has no
   local changes before building. Record the same reviewed commit when
   publishing the Windows enrollment controller; do not create a Docker image
   for it.
2. Copy `.env.example` to `.env`, fill every required non-secret value,
   including `WINDOWS_ENROLLMENT_CONTROLLER_IP`, and confirm no credential
   appears in it. Point `REMOTEHUB_CONFIG_FILE` to its root-owned rendered file
   and `REMOTEHUB_JOURNAL_DIR` to prepared persistent storage.
3. Review the fully resolved deployment before starting it:

   ```sh
   docker compose --env-file .env config
   docker compose --env-file .env build remotehub
   docker image inspect "stayactive-remotehub:<reviewed-source-revision>" \
     --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}'
   ```

   Confirm the RemoteHub image label equals its reviewed source revision. The
   resolved Compose output must contain no enrollment-controller service, no
   controller API-key bind mount, and no enrollment key file. Only Caddy
   publishes public TCP listeners; its TCP 4443 controller listener must remain
   bound to `127.0.0.1`.
4. After DNS, firewall, TLS, OIDC registration, and backup review, start the
   stack and validate the expected health endpoints:

   ```sh
   docker compose --env-file .env up -d
   docker compose --env-file .env exec headscale headscale configtest
   curl --fail --silent --show-error "https://remotehub.example.net/healthz"
   ```

   Install and start the Windows controller only after its private controller
   address, Caddy loopback CA trust, dedicated service identity, and one-peer
   firewall rule have been reviewed. Verify its health from the permitted Caddy
   path, verify that the Credential Manager target exists only in the service
   account profile without reading its value, and verify that a ticket issued
   through the tray expires in exactly 15 minutes. The Admin page is then
   available only at `https://remotehub.example.net/admin/`; complete its OIDC
   sign-in and verify it can create a test inventory mapping and show an audit
   record. Verify `https://${HEADSCALE_FQDN}/health`, inspect the client DERP
   map with `tailscale debug derp-map`, and confirm no public DERP regions are
   listed. On Windows clients, set `TS_NO_LOGS_NO_SUPPORT=true` before the first
   Headscale connection as defense-in-depth client log opt-out.

Do the initial MeshCentral administrator bootstrap locally and deliberately,
then retain `newAccounts: false`. There are no default accounts, passwords,
tokens, enrollment keys, or automatically trusted remote devices in this
scaffold.

## Safety and operations boundaries

- Enrollment, device visibility, screen viewing, exit-node routing, and file
  operations must remain individually authorized, auditable, and revocable.
  The Windows controller uses a separate scope/role and a Credential
  Manager-held Headscale key; this infrastructure does not grant blanket remote
  access.
- Direct requests to `https://<HEADSCALE_FQDN>/api/v1/*` must return `404` from
  every LAN/public source. The only administrative route is
  `headscale-controller.stayactive.test:4443` on the controller host's
  loopback interface. Test both boundaries after every Caddy or network change
  before issuing enrollment tickets.
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
