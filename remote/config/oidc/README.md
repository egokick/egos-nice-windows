# Operator-owned OIDC

OIDC is deliberately not enabled with placeholder credentials. Configure it
only in an operator-owned provider over HTTPS, with separate clients and
least-privilege scopes for each relying party.

## StayActive Windows tray: native public client

Register a dedicated **native public client** for the StayActive tray. It must
use Authorization Code flow with `S256` PKCE and the standard loopback callback
on `http://127.0.0.1:<dynamic-port>/`; for Keycloak, register exactly `http://127.0.0.1/` (including the trailing slash) so only the dynamic port varies. Require the provider's native-app
loopback support rather than registering a LAN callback or a static port.

Allow exactly these requested scopes:

```text
openid profile remotehub.fleet.read offline_access
```

The provider must issue an access token with the RemoteHub audience and
`remotehub.fleet.read`, plus a caller identity claim (`sub` or `client_id`).
The desktop app stores a granted refresh token with Windows DPAPI. Do not create
a client secret for this public client and do not use implicit, hybrid,
password, or client-credentials grants. Browser CORS is not a substitute for
native-client/loopback redirect controls.

## RemoteHub `/admin/`: browser public client

Register a different **browser public client** for the RemoteHub Admin page.
It must use Authorization Code flow with `S256` PKCE, no client secret, no
implicit/hybrid/password/client-credentials grants, and no refresh token.

Register these exact values:

```text
Redirect URI: https://<REMOTEHUB_FQDN>/admin/
Web origin:   https://<REMOTEHUB_FQDN>
Scopes:       openid profile remotehub.inventory.write remotehub.audit.read remotehub.admin
```

Issue an access token for the RemoteHub audience containing
`remotehub.inventory.write` and normally `remotehub.audit.read`, plus `sub` or
`client_id`. Do not grant or request `offline_access`. Allow the exact Admin
origin in the issuer's CORS policy for its discovery and token endpoints; the
page holds the access token only in memory. Set the public client ID in the
root-owned rendered RemoteHub production configuration, never a secret.

## Remotes enrollment: separate native public client

Register a third native public client named `stayactive-remotes-enrollment`.
It must use Authorization Code with mandatory `S256` PKCE, no client secret,
no implicit/hybrid/password/client-credentials grant, and no refresh token.
For Keycloak, register exactly the native loopback root:

```text
Redirect URI: http://127.0.0.1/
Scopes:       openid profile stayactive.enrollment.write
Audience:     stayactive-enrollment
```

Do not grant or request `offline_access`. Map only the dedicated
`stayactive.enrollment.write` scope and a flat multivalued `role` claim with
`stayactive.enrollment.admin` to device owners approved to add a computer. The
broker requires both values, and the StayActive **Remotes > Add device** flow
must obtain a fresh interactive authorization for this client rather than
reusing the tray's RemoteHub refresh token. This client must not be a browser
origin and must not receive RemoteHub inventory-administration scopes.

For the included LAN Keycloak realm, the idempotent
`configure-scope-mappings.sh` migration creates this scope, mapper, role, and
public client. Review the generated client and role assignment before exposing
the LAN endpoint.
## Headscale OIDC (separate server-side integration)

If Headscale OIDC is enabled, register its callback at
`https://<HEADSCALE_FQDN>/oidc/callback`, require PKCE with `S256`, and allow
only a dedicated operator group. This is a separate server-side integration and
may use a confidential client. Keep its secret only in a root-readable mounted
secret file such as `/run/secrets/headscale/oidc-client-secret`; do not put it
in `.env`, a Compose environment block, source control, or the StayActive
client.

MeshCentral SSO is another separate integration. Pin its server version first,
review the matching MeshCentral configuration schema, and map only
least-privilege operator groups. It remains disabled in this template rather
than enabling a partially configured identity path.
