# Opticon

Opticon is a privately administered Windows device command center. One Windows laptop is the primary controller; enrolled Windows PCs run a restricted background agent and can be remotely controlled, browsed, or used for file/media transfer according to their assigned role.

The design is intended for a small trusted fleet where the operator wants direct control of identity, authorization, and application credentials. It avoids router port forwarding and Microsoft RDP hosting, including on Windows Home. A small Headscale deployment on Fly.io gives roaming devices a stable rendezvous point; the command center and device-control authority remain on the operator's laptop.

> The source still uses `Taildesk.*` namespaces, data directories, tags, firewall rules, and scheduled-task names. Those are compatibility identifiers retained from the original package. **Opticon** is the product and executable name.

## System shape

| Component | Runs as | Responsibility |
| --- | --- | --- |
| **Opticon** | Signed-in primary Windows user | WPF UI, device inventory, invitations, role changes, revocation, transfers, one-click private remote sessions, and exit-node controls |
| **Coordinator** | Inside the Opticon tray process on `100.64.0.1:45830` | Accepts invitation enrollment, retains the local device registry, and synchronizes controller credentials |
| **Agent** | SYSTEM scheduled task on each managed PC, TCP `45831` | Restricted file/media API, inventory, enrollment completion, and credential rotation |
| **Tailscale client** | Windows service on every PC | WireGuard data plane and stable `100.64.0.0/10` addressing, pointed at the private Headscale server |
| **RustDesk engine** | Hidden Windows service on managed PCs; on-demand viewer on controllers | Desktop capture/input over direct IP at the peer's Tailscale address; no RustDesk ID, API, rendezvous, or relay service is used |
| **Fly Headscale service** | Always-on Fly Machine | Mesh identity/control plane, embedded DERP relay, and STUN; it does not run Opticon commands |

The primary command center is intentionally an interactive tray application, not a pre-logon Windows service. Locking the Windows session is fine; signing out or choosing **Exit** stops the coordinator until Opticon is started again.

## Enrollment and operation

1. Opticon asks Headscale for a tagged, single-use pre-authentication key.
2. It signs the personalized enrollment material, encrypts it with a random key, and copies a single-use URL with a default 14-day expiry to the clipboard. The decryption key is in the URL fragment, which browsers do not send to Fly.
3. The recipient opens the URL. The page downloads a tiny `Install-Opticon-<device>.cmd`; they open it and approve UAC. The starter downloads the reusable role-specific Opticon bundle from Fly, verifies its pinned size, SHA-256, and Authenticode signer, then starts Setup. Setup verifies the signed invitation and installs the pinned dependencies.
4. The new agent calls the laptop coordinator through its stable Tailscale address. The coordinator consumes the invitation, records the device, and supplies the final device-specific credentials.
5. Normal remote-control, file, and media traffic goes directly between peers when NAT traversal succeeds. If it cannot, the encrypted WireGuard traffic is relayed through the private DERP endpoint on Fly.

Invitation URLs contain a high-entropy identifier and a separate fragment decryption key. Fly stores the encrypted envelope, device label, role, and expiry; it never receives the fragment key or plaintext enrollment credentials. The default lifetime is 14 days. From the Invitations grid, right-click a row to copy its URL, extend its expiry, or expire it immediately. Extension preserves the URL but rotates the Headscale one-use key and re-signs/re-encrypts the payload. Successful enrollment immediately consumes the invitation, expires the local record, and removes the hosted ciphertext. Browsers deliberately do not auto-run downloads, so the unavoidable recipient flow is: open link, open the downloaded starter, approve UAC.

## Private remote-session boundary

RustDesk is retained as a replaceable open-source desktop engine rather than copied into the Opticon process. The RustDesk client is AGPL-3.0, and maintaining a private fork would add a large native/Rust/Flutter capture, codec, input, elevation, and accessibility surface. The process boundary keeps upgrades and license responsibilities explicit while Opticon owns the entire operator workflow.

On this command-center laptop, the RustDesk host service is disabled and there is no RustDesk tray application. Selecting a device and clicking **Remote into** makes Opticon:

1. require a `100.64.0.0/10` target and probe its direct port `21118`;
2. start the viewer for that Tailscale IP only;
3. fill the password field through Windows UI Automation, without putting the password on the command line or clipboard; and
4. leave only the actual remote-desktop session window visible.

On managed PCs, the RustDesk Windows service must run so unattended sessions and the Windows login screen can be captured. Its tray is hidden; public rendezvous, relay, discovery, update, and hole-punching features are disabled. Windows Firewall permits RustDesk traffic only to Tailscale IPv4 addresses and blocks all external IPv4 and IPv6 destinations. There is no RustDesk server account and no RustDesk-hosted control plane. RustDesk direct-IP framing is carried inside the encrypted Tailscale/WireGuard tunnel; when peers cannot communicate directly, the private DERP service on the operator-controlled Fly app relays that encrypted tunnel.

## Authorization and local state

Headscale tags separate managed-only devices, controllers, and the primary hub. The server policy limits which tagged nodes may reach agent/coordinator ports. Opticon adds per-device bearer tokens and RustDesk passwords as an application-layer boundary; those secrets are encrypted locally with Windows DPAPI.

Important compatibility locations:

- Admin configuration: `%LOCALAPPDATA%\Taildesk\Admin\admin.json`
- Agent configuration: `%PROGRAMDATA%\Taildesk\Agent\agent.json`
- Installed files: `%PROGRAMFILES%\Taildesk\...`
- Agent scheduled task: `Taildesk Agent`
- Coordinator firewall rule: `Taildesk Coordinator (Tailscale only)`
- Fly roaming task on this laptop: `Taildesk Fly Route`

The Fly route task updates only `213.188.217.227/32` through the active physical gateway at startup, sign-in, and every five minutes. This preserves control-plane reachability alongside NordVPN while leaving all other traffic on the normal VPN/default route.

The **System checks** page is the operational drift guard. It runs automatically after Opticon starts and can be rerun from the sidebar. It validates command-center identity and tags, DPAPI and certificate availability, signed control-plane administration, internet/Fly reachability, DNS and the dedicated route, the protected route task and pinned helper, exact dependency versions and Fly artifacts, coordinator/firewall isolation, RustDesk's controller-only posture, and installed shortcuts. It reports failures without displaying saved credentials.

## Fly.io control plane

The deployed app is `taildesk-egokick-control` at:

- Control API and DERP: `https://taildesk-egokick-control.fly.dev`
- Dedicated IPv4 / STUN: `213.188.217.227`, UDP `3478`
- Dedicated IPv6: `2a09:8280:1::15f:2c41:0`
- Region and VM: `ord`, `shared-cpu-1x`, 256 MB, always on
- Persistent state: encrypted `taildesk_data` volume mounted at `/var/lib/headscale`, with 14-day snapshots

The container runs Headscale `0.29.3` as UID/GID `65532`; both the Headscale and Go builder images are digest-pinned. An Opticon gateway on TCP `8080` exposes only required Tailscale protocol/DERP routes, the health check, and immutable pinned installer artifacts. Headscale itself listens only on loopback `8081`. Standard helper pages and the raw administrative API return 404. Opticon admin requests use timestamped HMAC-SHA256 signatures with one-use nonces and an exact endpoint allowlist; the long-lived Headscale bearer remains Fly-internal. Public Tailscale DERP maps, MagicDNS overrides, logtail, taildrop, and update checks are disabled.

Fly is responsible for:

- accepting required Headscale control connections and allowlisted, HMAC-authenticated Opticon administration;
- storing mesh users, nodes, pre-auth keys, Noise/DERP keys, and the ACL policy on the persistent volume;
- telling peers how to find one another;
- relaying already encrypted WireGuard packets when a direct route is unavailable;
- helping peers discover public endpoints through STUN;
- serving the four exact version/hash-pinned Tailscale and RustDesk installer artifacts used as the primary download source;
- storing reusable, signed role-specific Opticon bundles on the persistent volume and serving them by immutable filename;
- temporarily storing opaque encrypted invitation envelopes and serving their time-bounded, single-use landing pages.

Fly is **not** responsible for:

- the Opticon UI or device registry;
- issuing remote-control/file commands;
- receiving invitation fragment keys or plaintext RustDesk passwords, agent bearer tokens, enrollment keys, or shared files;
- decrypting peer traffic;
- routing this laptop's general internet traffic or acting as an exit node.

The Fly service is an external availability dependency for new registrations, peer discovery, and relay fallback. Existing direct peer sessions may survive a brief control-plane interruption, but the service should be treated as production infrastructure.

## Deploying Fly from this machine

The deployable directory is `fly-headscale`. Its `fly.toml`, Dockerfile, Headscale configuration, and policy are source-controlled together. The existing app, volume, and dedicated IPs should be updated in place—do not run `fly launch` or recreate them during a normal deployment.

Prerequisites:

- `flyctl` is installed through WinGet on this laptop.
- `C:\source\babelfish\.env` contains `FLY_API_TOKEN=...`.
- The expected IPs in `fly-headscale\config.yaml` still match `flyctl ips list`.
- The pinned Opticon signing certificate is available in the current user certificate store when rebuilding bundles.

From PowerShell:

```powershell
$tokenLine = Get-Content 'C:\source\babelfish\.env' |
    Where-Object { $_ -match '^FLY_API_TOKEN=' } |
    Select-Object -First 1
if (-not $tokenLine) { throw 'FLY_API_TOKEN was not found.' }

$env:FLY_API_TOKEN = ($tokenLine -split '=', 2)[1].Trim().Trim('"').Trim("'")
try {
    Set-Location 'C:\source\egos-nice-windows\Taildesk-source-1.0.0\fly-headscale'

    .\scripts\Build-OpticonBundles.ps1
    flyctl volumes snapshots create vol_re17jzg9qjylg034 --app taildesk-egokick-control
    flyctl deploy --remote-only --app taildesk-egokick-control --yes
    .\scripts\Publish-OpticonBundles.ps1
    flyctl status --app taildesk-egokick-control
    flyctl ips list --app taildesk-egokick-control
    flyctl volumes list --app taildesk-egokick-control
    Invoke-WebRequest 'https://taildesk-egokick-control.fly.dev/health' -UseBasicParsing
} finally {
    Remove-Item Env:\FLY_API_TOKEN -ErrorAction SilentlyContinue
}
```


The Docker build intentionally excludes the large Opticon ZIPs. `Build-OpticonBundles.ps1` signs them and updates the manifest; the small gateway deployment publishes that manifest; `Publish-OpticonBundles.ps1` then sends each ZIP in HMAC-authenticated 4 MiB chunks to the persistent Fly volume. The gateway accepts only filenames, sizes, and SHA-256 values already declared in the manifest and exposes a bundle only after full-file hash verification.

Useful diagnostics:

```powershell
flyctl logs --app taildesk-egokick-control
flyctl status --app taildesk-egokick-control --all
flyctl releases --app taildesk-egokick-control
```

Never copy the Fly token into this repository, `fly.toml`, the image, Opticon configuration, or an invitation. If the dedicated IPv4 changes, update `fly-headscale\config.yaml`, the DNS pin/route scripts under `scripts`, and the installed roaming task before considering the migration complete.

Fly CLI references: [deploy](https://fly.io/docs/flyctl/deploy/), [status](https://fly.io/docs/flyctl/status/), [IP management](https://fly.io/docs/flyctl/ips/).

## Build and install

From a Windows PowerShell prompt with the .NET 8 SDK:

```powershell
Set-Location 'C:\source\egos-nice-windows\Taildesk-source-1.0.0'
.\build.ps1 -Runtime win-x64
```

The build runs the solution and self-tests, publishes self-contained Windows binaries, and writes `dist\Opticon-CommandCenter-win-x64.zip`. Extract it and run `Install-Opticon.ps1` as Administrator. The installer preserves the compatibility data paths, creates Opticon desktop/startup/Start Menu shortcuts with the Opticon icon, installs the narrowly scoped roaming-route maintenance task, and removes legacy Taildesk shortcuts.

For deeper implementation details, continue with `docs\ARCHITECTURE.md` and `docs\SECURITY.md`, then read the code under `src\Taildesk.Admin`, `src\Taildesk.Agent`, `src\Taildesk.Setup`, and `src\Taildesk.Shared`.
