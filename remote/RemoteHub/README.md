# StayActive RemoteHub

RemoteHub is a deliberately small, self-hosted inventory service for the
StayActive Remotes tray UI. It maps a Headscale node ID to operator-approved,
opt-in metadata and capability policy. It is **not** a remote-control server.

It exposes no shell, process, browser, screen, file-content, Bluetooth, HCI,
passkey, or device-driver endpoint. A future screen/file/exit-node component
must separately enforce target consent and consume this inventory as policy
input; it cannot use RemoteHub as a generic command proxy.

## Production authentication and storage

RemoteHub validates standard OIDC access tokens using discovery from an
operator-owned HTTPS issuer. There is no local username/password endpoint and
there is no authentication fallback. Startup fails closed unless every required
setting below is present.

Set these values through the deployment's secret manager/environment, not a
checked-in JSON file:

```text
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5088

RemoteHub__Authentication__Authority=https://id.example.net/realms/stayactive
RemoteHub__Authentication__Audience=stayactive-remotehub

RemoteHub__Storage__JournalPath=/var/lib/stayactive-remotehub/inventory.journal.jsonl
RemoteHub__Storage__JournalHmacKey=<base64 of at least 32 random bytes>
```

`Authority` must be an absolute HTTPS OIDC issuer. Tokens must validate their
issuer, audience, signature, and expiry, include a `scope` or `scp` claim, and
identify the caller using `sub` or `client_id`. Discovery and signing-key
retrieval also require HTTPS.

The journal HMAC key is required even in development because it makes the
append-only audit chain tamper-evident. Keep the key in a separate secret store
and back it up separately from the journal. A loss of the key makes an existing
journal intentionally unreadable.

Mount the journal directory on encrypted, local persistent storage owned by the
service account. Run one RemoteHub writer per journal; it is not a clustered
database and must not use an SMB/NFS/network share. A damaged, truncated, or
HMAC-invalid journal prevents startup instead of silently discarding audit
history. Restore an operator-reviewed backup in that case.

Place the service on a private loopback/container network behind the
operator-controlled TLS reverse proxy. Do not expose its Kestrel HTTP listener
directly. Caddy/Headscale/MeshCentral deployment scaffolding lives in the
parent `remote/` directory, but this service deliberately does not alter that
scaffold or assume a hosted provider.

Optional scope-name overrides are available only when an operator needs a
different naming convention. Inventory and audit APIs also require the dedicated
administrator role; configure the issuer to emit that exact flat `role` claim
only for approved operators:

```text
RemoteHub__Authorization__FleetReadScope=remotehub.fleet.read
RemoteHub__Authorization__InventoryWriteScope=remotehub.inventory.write
RemoteHub__Authorization__AuditReadScope=remotehub.audit.read
RemoteHub__Authorization__AdministratorRole=stayactive.remotehub.admin
```

## Self-hosted Admin page (OIDC Authorization Code + PKCE)

RemoteHub includes an optional same-origin administrative page at
`https://<remotehub-public-origin>/admin/`. It is disabled unless explicitly
enabled, and cannot be enabled together with local-development header
authentication. The page uses a browser **public OIDC client** with
Authorization Code flow and mandatory `S256` PKCE:

- No client secret, password, API key, journal HMAC key, or bearer token is
  embedded in the page or returned by `/admin/config.json`.
- The authorization code verifier/state live only in `sessionStorage` while a
  sign-in redirect is in progress. The access token stays only in page memory;
  it is never written to `localStorage`, cookies, or the server, and the page
  does not request `offline_access`/refresh tokens.
- The page calls the existing protected inventory/audit APIs directly with its
  bearer token. It does not proxy authentication and has no remote-action,
  screen, file-content, exit-node, Bluetooth, HCI, passkey, or browser-control
  endpoint.

Configure it only behind the operator-controlled HTTPS reverse proxy:

```text
RemoteHub__AdminSpa__Enabled=true
RemoteHub__AdminSpa__PublicOrigin=https://remotehub.example.net
RemoteHub__AdminSpa__ClientId=stayactive-remotehub-admin
RemoteHub__AdminSpa__Scopes=openid profile remotehub.inventory.write remotehub.audit.read remotehub.admin
```

`PublicOrigin` must be exactly the external browser origin, with no path. The
derived, exact redirect URL is:

```text
https://remotehub.example.net/admin/
```

Register `stayactive-remotehub-admin` with the operator-owned OIDC provider as
a **public** client. Allow only the authorization-code grant, require PKCE with
`S256`, disable implicit/hybrid/password/client-credentials grants, do not
create a client secret, and register exactly the redirect URL above plus exactly
the `https://remotehub.example.net` web origin. Configure the provider to issue
an access token whose audience is
`RemoteHub__Authentication__Audience` and which has
`remotehub.inventory.write` (and normally `remotehub.audit.read`) for the
authorized administrator. The admin page verifies the provider's discovery
document advertises both `S256` and a no-secret (`none`) token-endpoint client
authentication mode before starting sign-in.

The browser must be allowed by the OIDC provider's CORS policy to fetch its
discovery and token endpoints from the exact Admin-page origin. RemoteHub's
strict Content Security Policy permits OIDC browser fetches only to the configured
issuer origin; use an issuer whose discovery/authorization/token endpoints share
that HTTPS origin. This fails safely rather than broadening page connections to
arbitrary domains.

The page displays mappings, creates/updates them with optimistic version checks,
and displays recent inventory audit summaries. It sends no device command. Its
assets/config response are `no-store`, deny embedding, deny referrers, and deny
web Bluetooth/USB/geolocation/camera/microphone permissions.

## API contract

All JSON uses camel-case enum strings. Every endpoint except `/healthz` requires
a valid OIDC bearer token and an appropriate scope.

| Endpoint | Required scope | Behaviour |
| --- | --- | --- |
| `GET /healthz` | none | Minimal liveness result only. |
| `GET /api/v1/fleet` | `remotehub.fleet.read` | Returns only verified device mappings. |
| `GET /api/v1/admin/inventory` | `remotehub.inventory.write` | Returns all mappings, including pending verification. |
| `PUT /api/v1/admin/inventory/{headscaleNodeId}` | `remotehub.inventory.write` | Creates/updates a single versioned mapping and appends its audit event. |
| `GET /api/v1/admin/audit?take=100` | `remotehub.audit.read` or inventory-write | Returns up to 500 immutable audit summaries, newest first. |

Fleet response:

```json
{
  "devices": [
    {
      "headscaleNodeId": "42",
      "ownerDisplayName": "Sam",
      "ownerDisplayNameOptIn": true,
      "coarseLocation": "Chicago, IL",
      "coarseLocationOptIn": true,
      "meshCentralNodeId": "mesh-node-id",
      "verified": true,
      "allowedCapabilities": ["ExitNode", "ScreenView", "SendFile", "RequestFile"],
      "version": 1,
      "updatedAtUtc": "2026-07-16T18:00:00+00:00"
    }
  ]
}
```

An admin must use optimistic concurrency. `expectedVersion: 0` creates a new
mapping only. An existing mapping must be updated with its exact current
version; a stale update returns `409` with `currentVersion`.

```json
{
  "expectedVersion": 0,
  "ownerDisplayNameOptIn": true,
  "ownerDisplayName": "Sam",
  "coarseLocationOptIn": true,
  "coarseLocation": "Chicago, IL",
  "meshCentralNodeId": "mesh-node-id",
  "verified": true,
  "allowedCapabilities": ["ExitNode", "ScreenView", "SendFile", "RequestFile"]
}
```

Names and locations are rejected unless the matching opt-in flag is true. The
service does not geolocate IP addresses: location must be a voluntarily supplied
coarse value such as city/region, never a street address. Screen/file
capabilities require a MeshCentral node ID; they still do not enable any action
through RemoteHub. `verified: false` devices remain invisible to the normal
fleet endpoint.

Audit records contain the sequence, actor subject, node ID, event/version,
timestamp, correlation ID, and a SHA-256 digest of the committed inventory
record—not the display name/location themselves. The on-disk journal combines
each immutable audit record and inventory event in one HMAC-chained append, so
an acknowledged update cannot exist without its audit record.

## Explicit local development/test mode

This is intentionally inconvenient to enable. It is allowed only when
`ASPNETCORE_ENVIRONMENT=Development` or `Testing`; setting it in `Production`
causes startup to fail. In Development, RemoteHub forcibly binds Kestrel to
loopback and accepts no non-loopback request.

```text
ASPNETCORE_ENVIRONMENT=Development
RemoteHub__LocalDevelopment__Enabled=true
RemoteHub__LocalDevelopment__Port=5097
RemoteHub__Storage__JournalPath=C:\temp\remotehub\inventory.journal.jsonl
RemoteHub__Storage__JournalHmacKey=<base64 of at least 32 random bytes>
```

Only in that explicit mode, tests/local tools may send:

```text
X-RemoteHub-Dev-Subject: local-operator
X-RemoteHub-Dev-Scopes: remotehub.inventory.write remotehub.fleet.read remotehub.audit.read
```

Those headers are never registered in Production and are not a password or a
deployable authentication mechanism.

## Build and test

```powershell
dotnet restore .\Tests\RemoteHub.Tests.csproj --locked-mode
dotnet build .\RemoteHub.csproj --no-restore
dotnet test .\Tests\RemoteHub.Tests.csproj --no-restore
```

The tests exercise optimistic concurrency, durable replay, tamper rejection,
production fail-closed configuration, Admin-SPA public configuration/static
asset headers, and the protected fleet/admin endpoint pipeline using ephemeral
loopback listeners.
