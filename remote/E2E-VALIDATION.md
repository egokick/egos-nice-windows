# StayActive Remotes: owner-run end-to-end validation

This checklist is intentionally separate from the automated test suite. A
successful build cannot prove that traffic exits a real remote network, that a
target user sees a consent prompt, or that a file reached a real target
unchanged. Run every gate below on infrastructure owned by the deployment
operator before enabling Remotes for normal use.

## Required test environment

- One operator-controlled Linux server with the rendered `remote/` deployment,
  public DNS/TLS, encrypted persistent storage, and backups.
- An operator-owned OIDC issuer and two distinct registered public clients:
  `stayactive-remotes-tray` for the Windows loopback flow, and the configured
  RemoteHub Admin client for `https://<remotehub-fqdn>/admin/`.
- Two separately connected Windows computers, each with the StayActive app,
  a Tailscale client enrolled only with the deployed Headscale server, and a
  MeshCentral agent enrolled with the operator's MeshCentral server.
- One computer configured and approved by the operator as an exit node. Use a
  different public network from the requesting computer.
- A small non-sensitive fixture file with a recorded SHA-256 hash. Do not use
  customer or credential material for transfer testing.
- An operator-owned HTTPS endpoint that reports the caller's observed source
  address. Avoid relying on a public IP-check service for this validation.

## Deployment and control-plane gates

1. Render every template to a root-owned file outside source control. Verify
   that `.env`, Compose configuration, image labels, and container inspection
   contain no OIDC secrets, RemoteHub journal HMAC key, MeshCentral encryption
   key, or enrollment key.
2. Run `docker compose --env-file .env config` and confirm that only Caddy
   exposes TCP 80/443 and Headscale exposes the deliberately selected UDP STUN
   port. RemoteHub and MeshCentral must not have host-published ports.
3. Confirm `https://<headscale-fqdn>/health` and
   `https://<remotehub-fqdn>/healthz` respond through the operator's TLS edge.
   Confirm each Windows client reports the self-hosted Headscale endpoint and
   `tailscale debug derp-map` contains no public Tailscale DERP region.
4. Register the two OIDC public clients exactly as documented in
   [`RemoteHub/README.md`](RemoteHub/README.md). Require Authorization Code
   plus S256 PKCE, prohibit a client secret for both public clients, and grant
   only the scopes required for their roles.
5. In `/admin/`, create an inventory mapping for each tagged Headscale node.
   Supply display name and coarse location only after the device owner has
   opted in. Set `verified` only after matching the node and MeshCentral agent
   identity. Confirm the resulting inventory and audit entry appear in the
   admin page.

## Client feature gates

1. On each Windows computer, configure the self-hosted Headscale, RemoteHub,
   OIDC, RemoteHub Admin, and MeshCentral HTTPS URLs in **Remotes settings**.
   Sign in through the tray's **Sign in to RemoteHub** action. Confirm the
   local protected token store is created for that Windows user and no bearer
   token is written to `settings.json`.
2. Open the tray **Remotes** submenu on both computers. It must show only
   `tag:stayactive` Headscale peers. Verify the computer name, opted-in owner
   display name, opted-in coarse location, online state, and centrally
   approved capabilities match the RemoteHub inventory.
3. Mark one mapping unverified in `/admin/`, refresh the other computer's tray
   menu, and confirm that screen, file, and new exit-node actions become
   unavailable. Restore the verified mapping and confirm the audit trail adds
   the update.
4. Select the approved exit node, accept the explicit routing disclosure, then
   call the operator-owned source-address endpoint. Its observed address must
   be the exit node's public address. Turn routing off from the same submenu,
   repeat the check, and confirm the direct public address is restored. Also
   verify that local-LAN access remains disabled while the exit route is on.
5. From a verified device, use **View screen**. Confirm MeshCentral requires
   the configured target-side permission/consent behavior, the target shows
   the session indicator, and the target can stop the session. Confirm the
   action fails from an unverified or offline entry.
6. Use **Send file** to transfer the fixture through MeshCentral. On the
   target, calculate its SHA-256 and compare it to the source hash. Then use
   **Request a file** and have the target user explicitly select and approve a
   fixture; compare the received hash. Confirm each user has only the
   MeshCentral file permissions intended for their role.
7. Sign out of RemoteHub in the tray, refresh, and confirm centrally gated
   actions disable. Sign in again and confirm actions return only after a
   successful inventory refresh.

## Negative and safety gates

- Attempt to configure a `tailscale.com` control-plane URL. The app must reject
  it; it must not fall back to Tailscale's hosted control plane or DERP map.
- Check that RemoteHub has no endpoint for shell execution, screen data, file
  content, HCI, passkeys, or arbitrary device commands.
- Verify the RemoteHub Admin page has no token persisted in browser storage,
  no client secret, and is served with its CSP/no-store/frame protections.
- Confirm that no part of the deployment virtualizes a Bluetooth adapter or
  forwards Bluetooth HCI/passkey material. Those capabilities are deliberately
  excluded; use a local authenticator or supported WebAuthn/RDP redirection.

Record the date, deployed image/source revision, device IDs, approver, and the
result of each gate in the operator's change record. A failed or skipped gate
means the Remotes deployment is not yet accepted for production use.
