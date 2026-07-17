# StayActive Remotes: owner-run end-to-end validation

This checklist is intentionally separate from the automated test suite. A
successful build cannot prove that traffic exits a real remote network, that a
target user sees a consent prompt, or that a file reached a real target
unchanged. Run every gate below on infrastructure owned by the deployment
operator before enabling Remotes for normal use.

## Required test environment

- One operator-controlled Windows Docker host with the rendered `remote/`
  deployment, public DNS/TLS, encrypted persistent storage, and backups. Its
  dedicated Windows enrollment-controller service runs on that same host so it
  can reach Caddy's controller-only loopback API.
- An operator-owned OIDC issuer and three distinct registered public clients:
  `stayactive-remotes-tray` for normal tray access, the configured RemoteHub
  Admin client for `https://<remotehub-fqdn>/admin/`, and
  `stayactive-remotes-enrollment` for fresh one-time device enrollment.
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
   key, Headscale controller API key, or enrollment key. Confirm there is no
   Docker enrollment-controller service and no container API-key mount or key
   file. Verify the controller key exists only as
   `StayActive/HeadscaleController/v1` in the dedicated Windows service-account
   Credential Manager profile; do not read or export its value. Verify that the
   controller binary, configuration, journal, and integrity material are under
   `%ProgramData%\StayActiveRemotes\EnrollmentController` with no write access
   for the interactive tray user or ordinary users; they must not reside under
   `remote/lan-test/state`.
2. Run `docker compose --env-file .env config` and confirm that only Caddy
   exposes public TCP 80/443 and Headscale exposes the deliberately selected
   UDP STUN port. Caddy's TCP 4443 Headscale controller API listener must be
   host-loopback only. RemoteHub and MeshCentral must not have host-published
   ports; the Windows controller's TCP 5091 firewall rule must permit only
   Caddy's fixed private peer.
3. Confirm `https://<headscale-fqdn>/health` and
   `https://<remotehub-fqdn>/healthz` respond only through their intended
   public paths. From every LAN/public source, request
   `https://<headscale-fqdn>/api/v1/users`; it must return `404`, not Headscale
   authentication or API output. On the controller host only, confirm
   `headscale-controller.stayactive.test:4443` resolves to loopback and that no
   firewall rule publishes TCP 4443. Repeat after every Caddy reload.

4. Register the three OIDC public clients exactly as documented in
   [`config/oidc/README.md`](config/oidc/README.md). Require Authorization Code
   plus S256 PKCE, prohibit a client secret for all public clients, and grant
   only the scopes/audiences/roles required for their separate roles.
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
2. On one trusted operator laptop, choose **Remotes > Add device**. Confirm it
   obtains a fresh authorization for `stayactive-remotes-enrollment`, creates a
   one-use ticket that expires exactly 15 minutes after issuance, and shows the
   raw ticket only at the moment it is issued. Redeem it on the second laptop
   once; retrying the same ticket must fail. Verify the Windows controller
   journal/audit records ticket status but never stores the raw
   pre-authentication key.
3. Open the tray **Remotes** submenu on both computers. It must show only
   `tag:stayactive` Headscale peers. Verify the computer name, opted-in owner
   display name, opted-in coarse location, online state, and centrally
   approved capabilities match the RemoteHub inventory.
4. Mark one mapping unverified in `/admin/`, refresh the other computer's tray
   menu, and confirm that screen, file, and new exit-node actions become
   unavailable. Restore the verified mapping and confirm the audit trail adds
   the update.
5. Select the approved exit node, accept the explicit routing disclosure, then
   call the operator-owned source-address endpoint. Its observed address must
   be the exit node's public address. Turn routing off from the same submenu,
   repeat the check, and confirm the direct public address is restored. Also
   verify that local-LAN access remains disabled while the exit route is on.
6. From a verified device, use **View screen**. Confirm MeshCentral requires
   the configured target-side permission/consent behavior, the target shows
   the session indicator, and the target can stop the session. Confirm the
   action fails from an unverified or offline entry.
7. Use **Send file** to transfer the fixture through MeshCentral. On the
   target, calculate its SHA-256 and compare it to the source hash. Then use
   **Request a file** and have the target user explicitly select and approve a
   fixture; compare the received hash. Confirm each user has only the
   MeshCentral file permissions intended for their role.
8. Sign out of RemoteHub in the tray, refresh, and confirm centrally gated
   actions disable. Sign in again and confirm actions return only after a
   successful inventory refresh.

## Negative and safety gates

- Attempt to call `POST /api/v1/enrollment-tickets` with the normal tray token,
  a token missing `stayactive.enrollment.write`, a token missing
  `stayactive.enrollment.admin`, an expired token, and no token. Each must be
  denied. Attempt to request another lifetime: the controller must reject it or
  still return the fixed one-use 15-minute policy. Verify no response, server
  log, journal, or ticket-status endpoint returns the raw Headscale
  pre-authentication key after initial issuance.
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
