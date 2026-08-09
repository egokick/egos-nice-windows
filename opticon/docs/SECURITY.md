# Security model

## Trust boundaries

- The primary laptop and Tailscale control plane decide role/tag membership.
- Taildesk's bearer token is a second application-layer check, not a replacement for Tailscale grants.
- Managed-only machines receive no source grant to Agent, RustDesk, or SSH ports. They can reach the hub's coordinator port only so a one-time invite can enroll; registry sync separately requires a valid controller token and a current controller role.
- Nothing listens on a public or LAN IP by design. Do not add router port forwarding.

## File API controls

- Requests name a configured root ID and relative path; arbitrary absolute paths are unsupported.
- Rooted, UNC, device, alternate-data-stream, and escaping paths are rejected.
- Ready local volumes are opened as verified Windows directory handles. Listings, reads, creates, promotions, and deletions resolve relative to those handles with reparse traversal disabled, so renaming or replacing a checked pathname cannot redirect a later SYSTEM operation. The authenticated controller can browse any directory permitted by the Agent's Windows account; mapped network drives are not exposed as device volumes.
- Uploads stream into a random partial file created relative to the verified destination handle, flush, then promote that same handle without overwriting an unexpected destination. Canceled uploads delete the partial through its verified handle.
- Two concurrent uploads are allowed per Agent. Declared length is mandatory; the default ceiling is 256 GiB, a 5 GiB disk reserve is enforced, bytes cannot exceed the declaration, and each upload has a 24-hour cancellation deadline. This permits files larger than 20 GiB without permitting unbounded requests.
- Media URLs carry an HMAC over HTTP method, root, relative path, expiry, and nonce, expire after five minutes, and revalidate the path when opened.

## Invite controls

- Headscale invitation keys are non-reusable, preauthorized, tagged, and share the signed invitation's expiry (14 days by default, bounded to 365 days).
- A local role edit cannot upgrade access because the tag is embedded in the auth key and the coordinator uses its stored invitation role.
- The OAuth client secret and other devices' secrets never enter a bundle.
- The invitation secret is stored as a hash on the hub. The target keeps it DPAPI-protected only until enrollment succeeds.
- Enrollment and cancellation share one state gate. Enrollment queries Headscale and requires an exact match among the request source Tailscale IP, reported Tailscale IP, Headscale node ID/user, invitation role tags, and exit-node tag. Existing node-ID or Tailscale-IP collisions are rejected instead of updated.
- The personalized payload is RSA-PSS signed and AES-GCM encrypted. Fly receives only ciphertext; the decryption key remains in the URL fragment and is not included in HTTP requests.
- Reusable role bundles are private S3 objects readable only by a specific CloudFront Origin Access Control distribution. The Fly-hosted manifest permits only exact HTTPS CloudFront URLs with safe immutable version/file paths; the starter verifies pinned size, SHA-256, and the Setup Authenticode certificate; Setup verifies the signed invitation and signed Agent/Admin payloads. Direct S3 access is blocked, and neither S3 nor CloudFront is trusted for executable integrity.
- Extending an active invitation preserves its URL while rotating the one-use Headscale key and replacing the signed encrypted payload. Manual expiry revokes the key and deletes the ciphertext.
- Successful enrollment durably commits the device and marks the local invitation redeemed before revoking its key and deleting the Fly object. If the success response is lost, only an exact retry with the same invitation secret, node identity, address, host, OS, and Agent version is accepted; all different replays remain rejected. The command center retries hosted-object cleanup after a transient failure.

## RustDesk controls

- A unique permanent password is generated per target.
- Direct-IP access is enabled on managed devices at port 21118; LAN discovery, public rendezvous/relay, automatic updates, UDP/IPv6 punching, and remote configuration changes are disabled.
- RustDesk's whitelist is restricted to `100.64.0.0/10`; Tailscale grants provide the actual controller/managed authorization.
- Windows Firewall allows the direct port only at the target's Tailscale local IP and only from the Tailscale address range. Outbound RustDesk traffic to every non-Tailscale IPv4 destination and all IPv6 destinations is blocked.
- Managed targets permit RustDesk's Windows privacy mode. Each command center exposes it as an opt-in per-device setting; ordinary connections mirror the physical display by default so they do not depend on a virtual-display driver.
- Opticon launches RustDesk against the selected Tailscale IP and supplies the saved per-device password through RustDesk's supported connection command. The password is not copied to the clipboard during the normal workflow; manual clipboard copy remains a labeled recovery action.

## Administrative SSH controls

- Just-in-time SSH listens only on the target's Tailscale IPv4 address at TCP `45832`. Headscale grants admit only `tag:taildesk-hub` and Windows Firewall narrows that further to the configured primary hub's exact Tailscale address.
- The authenticated Agent endpoint records bounded leases and revocation tombstones; the stable, signed SYSTEM Guardian lives outside the swappable Agent directory and independently owns the isolated `sshd` process.
- Every session uses a new Ed25519 key, a pinned per-target host key, source and UTC expiry restrictions, disabled password/keyboard-interactive authentication, and disabled forwarding. The dedicated administrator account is disabled when no lease is active.
- The Guardian refreshes authorized keys every five seconds and contains `sshd` plus authenticated children in a kill-on-close Job Object. Revoking or expiring any lease advances a protected termination generation and restarts that job, closing already-authenticated shells even when another lease remains; clients with a still-valid lease may reconnect. Invalid state or lock timeout fails closed. Process loss or reboot kills the contained process tree; the startup task reopens access only if a protected, nonexpired lease still validates.
- No Agent endpoint accepts a command, script, executable path, or shell fragment. Administrative commands occur only inside the operator-opened, host-key-pinned SSH session.

## Remote update controls

- Normal remote updates contain only the signed target Agent/runtime payload. They never run Setup remotely or replace the stable Guardian, Tailscale, RustDesk, Windows OpenSSH, identity, credentials, tags, routes, or policy.
- The update verifier enforces the signed release's minimum Guardian version. The stable Guardian verifies its own signature, the exact Agent task and paths, the signed candidate, rollback capacity, and the three recovery lifelines: Tailscale reachability, RustDesk, and SSH when active.
- A distinct signed SYSTEM watchdog checks nonterminal durable transactions every minute and closes a producer-crash gap before explicit Guardian startup. It never handles terminal boot health, uses a three-second transaction-lock attempt, and cannot suppress the full ONSTART Guardian because full mode waits through a quick watchdog.
- Activation durably journals each phase, retains the installed Agent as last-known-good, and requires repeated local plus command-center health before an idempotent commit. Crash, reboot, sustained health loss, or a missed commit deadline restores last-known-good.
- Legacy maintenance verifies the RSA-PSS-signed inner manifest and its exact root Setup size, SHA-256, and publisher declaration before UAC; elevated Setup re-verifies the same declaration and a pinned Authenticode signature. Setup has no commit authority: only the originating command center may commit after three exact authenticated external samples tied to the copied operation, release, device identity, RustDesk, and any active SSH lifeline.
- Devices that predate this design require one signed, attended maintenance bootstrap through their existing RustDesk session. Stable-Guardian maintenance remains a separate attended operation; it is never smuggled into an Agent update.

## Remaining risks

- A local administrator on any PC can replace binaries, read machine-level secrets, or install another remote-control product. Taildesk does not defend a machine against its own local administrators.
- An authorized Opticon SSH shell is deliberately a full elevated local-administrator session and can create persistent machine changes. Ephemeral keys, source-IP policy, attestation, revocation, and lease expiry limit access credentials and exposure time; they cannot contain commands already authorized to run as administrator. Grant CLI/UI access only to trusted operators and agents.
- The main build artifacts are not publisher-trusted until you apply an organization code-signing certificate. Personalized invitation EXEs are Authenticode-signed with the locally controlled pinned invitation certificate and carry a separately verified signed payload.
- Registry sync never contains permanent agent/RustDesk credentials. A secondary controller can request credentials only for one device at a time after controller-token plus Tailscale source-IP authentication and an explicit `AuthorizedControllerIds` grant on that target. Revoking the grant or controller tag stops future retrieval/reachability.
- The coordinator uses HTTP inside the already encrypted Tailscale tunnel. Its bind address and firewall rule must remain Tailscale-only; enrollment and registry clients bypass ambient system proxies and reject redirects so invitation secrets and controller bearers stay on the direct Tailscale path.
- Role, credential-rotation, and exit-node Agent actions additionally require the request to originate from the configured primary hub's Tailscale IP.
- Credential rotation is a durable operation-ID transaction. The command center saves the pending credentials first, the Agent temporarily permits the previous token only to replay that exact rotation, and an explicit commit retires the old token. Either side can recover after a lost response or restart without losing the only working token/password pair.
- Fly exposes only required Tailscale protocol routes, immutable pinned installers, health, the tiny no-store release manifest, and an HMAC-authenticated exact administrative allowlist. The Headscale bearer is Fly-internal; helper pages and raw /api routes return 404. Large new bundles bypass Fly and use S3/CloudFront transport, while all existing cryptographic verification remains mandatory. Headscale nodes have no default expiry, so long-offline durable tagged machines can reconnect automatically.
- The coordinator is not a Windows service. Its availability depends on the primary interactive user remaining signed in with Taildesk running; locking that session is supported. Do not enable insecure automatic Windows logon merely to approximate pre-logon service availability.
- Command-center installation may be elevated with a different administrator identity, but shortcuts and Tailscale operator access are resolved to the Explorer owner in the invoking session. Admin itself must subsequently be launched unelevated by that interactive user so its current-user DPAPI secrets and configuration stay in the intended profile.

## Release checklist

1. Build on a clean, patched Windows runner.
2. Run `Taildesk.SelfTest` and test real Windows Home targets.
3. Sign Admin, Agent, Setup, and the stable Guardian with Authenticode; timestamp signatures.
4. Scan all published binaries.
5. Confirm the tailnet policy tests pass and no allow-all ACL/grant remains.
6. Verify LAN IPs do not expose ports 45830, 45831, 45832, or 21118; verify TCP 45832 accepts only the exact primary hub over Tailscale.
7. Test SSH expiry/revocation and supervisor termination, forced update rollback, durable commit, reboot/logoff, upload cancellation, expired/canceled invite races, role demotion, device removal, and offline credential/shortcut synchronization.
8. From a standard-user desktop, install with different administrator credentials at UAC; verify the shortcut, startup entry, Admin data, and Tailscale operator all belong to the original interactive user.
