# Security model

## Trust boundaries

- The primary laptop and Tailscale control plane decide role/tag membership.
- Taildesk's bearer token is a second application-layer check, not a replacement for Tailscale grants.
- Managed-only machines receive no source grant to Agent or RustDesk ports. They can reach the hub's coordinator port only so a one-time invite can enroll; registry sync separately requires a valid controller token and a current controller role.
- Nothing listens on a public or LAN IP by design. Do not add router port forwarding.

## File API controls

- Requests name a configured root ID and relative path; arbitrary absolute paths are unsupported.
- Rooted, UNC, device, alternate-data-stream, and escaping paths are rejected.
- Existing path components with `FileAttributes.ReparsePoint` are rejected to block symlink/junction escapes.
- Uploads stream into a random partial file inside the validated destination, flush, then atomically move into place. Canceled uploads remove the partial file.
- Two concurrent uploads are allowed per Agent. Declared length is mandatory; the default ceiling is 256 GiB, a 5 GiB disk reserve is enforced, bytes cannot exceed the declaration, and each upload has a 24-hour cancellation deadline. This permits files larger than 20 GiB without permitting unbounded requests.
- Media URLs carry an HMAC over HTTP method, root, relative path, expiry, and nonce, expire after five minutes, and revalidate the path when opened.

## Invite controls

- Headscale invitation keys are non-reusable, preauthorized, tagged, and share the signed invitation's expiry (14 days by default, bounded to 365 days).
- A local role edit cannot upgrade access because the tag is embedded in the auth key and the coordinator uses its stored invitation role.
- The OAuth client secret and other devices' secrets never enter a bundle.
- The invitation secret is stored as a hash on the hub. The target keeps it DPAPI-protected only until enrollment succeeds.
- Enrollment and cancellation share one state gate. Enrollment queries Headscale and requires an exact match among the request source Tailscale IP, reported Tailscale IP, Headscale node ID/user, invitation role tags, and exit-node tag. Existing node-ID or Tailscale-IP collisions are rejected instead of updated.
- The personalized payload is RSA-PSS signed and AES-GCM encrypted. Fly receives only ciphertext; the decryption key remains in the URL fragment and is not included in HTTP requests.
- Reusable role bundles are served from the operator-controlled Fly app. The starter verifies pinned size, SHA-256, and the Setup Authenticode certificate; Setup verifies the signed invitation and signed Agent/Admin payloads.
- Extending an active invitation preserves its URL while rotating the one-use Headscale key and replacing the signed encrypted payload. Manual expiry revokes the key and deletes the ciphertext.
- Successful enrollment marks the local invitation redeemed and expired, revokes its key, and deletes the Fly object with bounded retries. The command center retries cleanup on refresh after a transient Fly failure; coordinator one-use state rejects any replay regardless.

## RustDesk controls

- A unique permanent password is generated per target.
- Direct-IP access is enabled on managed devices at port 21118; LAN discovery, public rendezvous/relay, automatic updates, UDP/IPv6 punching, and remote configuration changes are disabled.
- RustDesk's whitelist is restricted to `100.64.0.0/10`; Tailscale grants provide the actual controller/managed authorization.
- Windows Firewall allows the direct port only at the target's Tailscale local IP and only from the Tailscale address range. Outbound RustDesk traffic to every non-Tailscale IPv4 destination and all IPv6 destinations is blocked.
- Opticon launches RustDesk against the selected Tailscale IP and fills its password control through Windows UI Automation. The password is not placed in process arguments or on the clipboard during the normal workflow; manual clipboard copy remains a labeled recovery action.

## Remaining risks

- A local administrator on any PC can replace binaries, read machine-level secrets, or install another remote-control product. Taildesk does not defend a machine against its own local administrators.
- The main build artifacts are not publisher-trusted until you apply an organization code-signing certificate. Personalized invitation EXEs are Authenticode-signed with the locally controlled pinned invitation certificate and carry a separately verified signed payload.
- Registry sync never contains permanent agent/RustDesk credentials. A secondary controller can request credentials only for one device at a time after controller-token plus Tailscale source-IP authentication and an explicit `AuthorizedControllerIds` grant on that target. Revoking the grant or controller tag stops future retrieval/reachability.
- The coordinator uses HTTP inside the already encrypted Tailscale tunnel. Its bind address and firewall rule must remain Tailscale-only.
- Role, credential-rotation, and exit-node Agent actions additionally require the request to originate from the configured primary hub's Tailscale IP.
- Fly exposes only required Tailscale protocol routes, immutable pinned installers, health, and an HMAC-authenticated exact administrative allowlist. The Headscale bearer is Fly-internal; helper pages and raw /api routes return 404. Headscale nodes have no default expiry, so long-offline durable tagged machines can reconnect automatically.
- The coordinator is not a Windows service. Its availability depends on the primary interactive user remaining signed in with Taildesk running; locking that session is supported. Do not enable insecure automatic Windows logon merely to approximate pre-logon service availability.
- Command-center installation may be elevated with a different administrator identity, but shortcuts and Tailscale operator access are resolved to the Explorer owner in the invoking session. Admin itself must subsequently be launched unelevated by that interactive user so its current-user DPAPI secrets and configuration stay in the intended profile.

## Release checklist

1. Build on a clean, patched Windows runner.
2. Run `Taildesk.SelfTest` and test real Windows Home targets.
3. Sign Admin, Agent, and Setup with Authenticode; timestamp signatures.
4. Scan all published binaries.
5. Confirm the tailnet policy tests pass and no allow-all ACL/grant remains.
6. Verify LAN IPs do not expose ports 45830, 45831, or 21118.
7. Test reboot/logoff, upload cancellation, expired/canceled invite races, role demotion, device removal, and offline credential/shortcut synchronization.
8. From a standard-user desktop, install with different administrator credentials at UAC; verify the shortcut, startup entry, Admin data, and Tailscale operator all belong to the original interactive user.
