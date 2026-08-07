# Architecture

## Components

| Component | Runs as | Purpose |
|---|---|---|
| Taildesk Admin | Signed-in Windows user | Native WPF UI, inventory polling, invitations, transfers, role/exit-node control |
| Coordinator server | Inside primary Admin process | Enrollment on TCP 45830 and revocable registry sync for secondary controllers |
| Taildesk Agent | SYSTEM startup task | Restricted HTTP API on the machine's Tailscale IPv4 address, TCP 45831 |
| Taildesk Setup | Elevated, once | Installs dependencies and payload, joins tailnet, applies hardening, creates startup task/rules |
| RustDesk engine | Hidden service on managed devices; on-demand viewer on controllers | Direct-IP remote screen/control on TCP 21118, restricted to Tailscale |
| Tailscale | Service | WireGuard transport, MagicDNS/addressing, grants, tags, exit routing |

The command-center installer resolves the Explorer owner in the invoking Windows session rather than trusting the elevated process identity. This keeps shortcuts and Tailscale CLI operator access attached to the intended interactive user when that user supplies a different administrator account at an over-the-shoulder UAC prompt. The installer does not launch Admin under the elevated token.

## Enrollment sequence

1. Admin requests a non-reusable, preauthorized, tagged Headscale key with the invitation's default 14-day expiry.
2. Admin generates per-device Agent/RustDesk/controller credentials and an invitation secret, signs the payload, encrypts it with a random URL-fragment key, and uploads only the ciphertext to Fly.
3. The recipient URL generates a tiny starter that downloads a reusable role-specific bundle and verifies its pinned size, SHA-256, and Authenticode signer before launching Setup.
4. Setup decrypts and verifies the invitation, checks expiry, installs verified dependencies, consumes the key with `tailscale up --auth-key=â€¦ --hostname=â€¦ --unattended=true`, and verifies the exact tailnet plus mutually exclusive role/exit tags.
5. Setup binds Agent and Windows Firewall rules to the assigned `100.x` address.
6. Agent repeatedly posts the invitation secret and its actual Tailscale identity to the coordinator until accepted.
7. Coordinator checks invitation hash, expiry, one-use state, remote Tailscale source IP, and reported address, then durably commits the device and marks the invitation redeemed/expired before revoking the auth key and deleting its hosted ciphertext. An exact retry after a lost response returns the already-committed success.
8. Agent deletes its pending enrollment secret; Setup deletes `invite.tdinvite`.

If Tailscale already has an identity, a first install pauses for explicit permission before logging it out; this prevents an unused invite key from surviving an apparent acceptance. A partial retry may reuse only the same invitation's recorded session, and only after exact tailnet/role/exit-tag verification. Install state records the completed invite ID, making a retry after a late enrollment idempotent.

An active invitation can be extended from the grid without changing its URL. Opticon downloads and verifies its encrypted payload, creates a replacement one-use Headscale key, re-signs/re-encrypts the payload with the later expiry, publishes it atomically, and revokes the superseded key. Manual expiry revokes the key and removes the hosted ciphertext immediately.

## Runtime paths

- Admin settings: `%LOCALAPPDATA%\Taildesk\Admin\admin.json`
- Agent settings: `%PROGRAMDATA%\Taildesk\Agent\agent.json`
- Installed executables: `%PROGRAMFILES%\Taildesk\...`
- Agent task: `Taildesk Agent`, `ONSTART`, `SYSTEM`, highest privileges

Admin secrets use current-user DPAPI. The Agent stores only the SHA-256 hash of its random 256-bit bearer token; its temporary enrollment secret and media-signing key use machine DPAPI. ProgramData Agent ACLs are reduced to SYSTEM and Administrators.

## Coordinator availability

The coordinator is hosted inside the primary user's tray-resident WPF process. It begins at that user's Windows sign-in through a Startup shortcut and can continue while the screen is locked. It is unavailable before sign-in, after sign-out, after the tray **Exit** command, or if the Admin process fails. Targets and Tailscale use startup services/tasks, but new enrollment and secondary-controller registry sync require the primary Admin process.

Making the coordinator truly pre-logon is not a task-scheduler switch: current primary state and OAuth material are current-user DPAPI data, and the WPF process owns both state mutations and the listener. A service implementation needs machine-scoped coordinator state, a narrow authenticated IPC contract, synchronization with the UI, service recovery, and migration of existing secrets. This release intentionally does not store the user's Windows password or run the WPF executable in session 0 as a workaround.

## Role changes

Tailscale tags are authoritative. Editing a local JSON role cannot grant network reachability.

- Promotion adds `tag:taildesk-controller`, changes the Agent's fixed role state, and creates common Taildesk controller shortcuts.
- Demotion first changes the tag to `tag:taildesk-managed`, so access is revoked at the network layer. It then disables controller shortcuts and marks every peer credential for rotation. Offline shortcut cleanup and credential rotation remain queued until the relevant Agent reconnects.

Controller and managed tags are mutually exclusive. Exit-provider capability is the orthogonal `tag:taildesk-exit` tag.

## Local versus internet connections

Every peer address is a Tailscale `100.x` address. On one LAN, Tailscale normally establishes a direct peer-to-peer path over that LAN. Across the internet it establishes a direct NAT-traversed path when possible and uses a DERP relay otherwise. The Taildesk application configuration does not change between those cases.
