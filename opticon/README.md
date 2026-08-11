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
| **Stable Guardian** | SYSTEM scheduled tasks outside the versioned Agent directory | Guards transactional Agent swaps and independently owns isolated just-in-time SSH on target Tailscale TCP `45832` |
| **Tailscale client** | Windows service on every PC | WireGuard data plane and stable `100.64.0.0/10` addressing, pointed at the private Headscale server |
| **RustDesk engine** | Hidden Windows service on managed PCs; on-demand viewer on controllers | Desktop capture/input over direct IP at the peer's Tailscale address; no RustDesk ID, API, rendezvous, or relay service is used |
| **Fly Headscale service** | Always-on Fly Machine | Mesh identity/control plane, embedded DERP relay, and STUN; it does not run Opticon commands |

The primary command center is intentionally an interactive tray application, not a pre-logon Windows service. Locking the Windows session is fine; signing out or choosing **Exit** stops the coordinator until Opticon is started again.

## Enrollment and operation

1. Opticon asks Headscale for a tagged, single-use pre-authentication key.
2. It signs the personalized enrollment material, encrypts it with a random key, and copies a single-use URL with a default 14-day expiry to the clipboard. The decryption key is in the URL fragment, which browsers do not send to Fly.
3. The recipient opens the URL. The page downloads the exact versioned source archive and signed bootstrap from CloudFront, verifies both byte counts and SHA-256 hashes in browser WebCrypto, and gives the bootstrap an invite-bearing filename. The recipient keeps both files together, opens `Install-Opticon-<id>--<key>--<bootstrap-sha256>.exe`, and approves UAC. The bootstrap rechecks its own hash and production signature, decrypts and verifies the signed invitation, verifies the source archive and its RSA-PSS manifest, and builds the requested x64 or ARM64 payload locally with the exact pinned .NET SDK.
4. The new agent calls the laptop coordinator through its stable Tailscale address. The coordinator consumes the invitation, records the device, and supplies the final device-specific credentials.
5. Normal remote-control, file, and media traffic goes directly between peers when NAT traversal succeeds. If it cannot, the encrypted WireGuard traffic is relayed through the private DERP endpoint on Fly.

Invitation URLs contain a high-entropy identifier and a separate fragment decryption key. Fly stores the encrypted envelope, device label, role, expiry, and exact authenticated release metadata; it never receives the fragment key or plaintext enrollment credentials. The default lifetime is 14 days. From the Invitations grid, right-click a row to copy its URL, extend its expiry, or expire it immediately. Extension preserves the URL but rotates the Headscale one-use key and re-signs/re-encrypts the payload. Successful enrollment immediately consumes the invitation, expires the local record, and removes the hosted ciphertext. Browsers deliberately do not auto-run downloads, so the recipient flow is: open the link, download both authenticated files, keep them together, open the signed bootstrap, and approve UAC.

Elevated source verification, build state, and redacted setup diagnostics stay in ACL-protected machine storage rather than user-writable profile paths. Invitation fragment keys are redacted from arguments, exceptions, and persisted diagnostics.

The invitation page requires a current browser with WebCrypto SHA-256. There is no unsigned compatibility script or length-only fallback. If no stable .NET 10 SDK (`10.*.*`) is installed, Setup shows the fixed official Microsoft .NET 10 URL and keeps checking until one is available; elevated code never opens a browser or executes an SDK installer. The isolated offline build disables user MSBuild imports and online package sources, produces self-contained attested output, then Setup automatically consumes the one-time invitation to join the private Headscale mesh.

## Private remote-session boundary

RustDesk is retained as a replaceable open-source desktop engine rather than copied into the Opticon process. The RustDesk client is AGPL-3.0, and maintaining a private fork would add a large native/Rust/Flutter capture, codec, input, elevation, and accessibility surface. The process boundary keeps upgrades and license responsibilities explicit while Opticon owns the entire operator workflow.

On this command-center laptop, the RustDesk host service is disabled and there is no RustDesk tray application. Selecting a device and clicking **Remote into** makes Opticon:

1. require a `100.64.0.0/10` target and probe its direct port `21118`;
2. start the viewer for that Tailscale IP only;
3. fill the password field through Windows UI Automation, without putting the password on the command line or clipboard; and
4. leave only the actual remote-desktop session window visible.

On managed PCs, the RustDesk Windows service must run so unattended sessions and the Windows login screen can be captured. Its tray is hidden; public rendezvous, relay, discovery, update, and hole-punching features are disabled. Windows Firewall permits RustDesk traffic only to Tailscale IPv4 addresses and blocks all external IPv4 and IPv6 destinations. There is no RustDesk server account and no RustDesk-hosted control plane. RustDesk direct-IP framing is carried inside the encrypted Tailscale/WireGuard tunnel; when peers cannot communicate directly, the private DERP service on the operator-controlled Fly app relays that encrypted tunnel.

## Remote maintenance and guarded updates

Opticon protects three recovery lifelines on each managed PC: Tailscale reachability, RustDesk direct remote desktop, and just-in-time administrative SSH. **Open SSH** creates an ephemeral, host-key-pinned lease on TCP `45832`, bound to the target's Tailscale IPv4 address. Headscale policy and Windows Firewall admit only the primary hub's exact Tailscale address; the stable SYSTEM Guardian owns the isolated `sshd` process and expires access independently of the versioned Agent.

**Update Opticon** installs only a signed, role- and architecture-matched Agent/runtime payload on the target. It does not run Setup remotely or replace the stable Guardian, Tailscale, RustDesk, Windows OpenSSH, device identity, credentials, tags, routes, or policy. The Guardian keeps the installed Agent as last-known-good, activates the candidate, and commits it only after repeated authenticated health checks confirm all applicable lifelines. A crash, reboot, lost lifeline, or missed commit rolls back to last-known-good.

Devices enrolled before this maintenance architecture need one signed, attended bootstrap through their existing RustDesk session to install the stable Guardian, OpenSSH containment, and update protocol. Later Agent/runtime releases use the guarded path. Opticon exposes no arbitrary remote-execution Agent API; agents and scripts use the same just-in-time administrative SSH lease through the installed `opticon` CLI. See [`docs/REMOTE-ADMINISTRATION.md`](docs/REMOTE-ADMINISTRATION.md) for exact commands, lease behavior, and rollout details.

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
On this command-center laptop, NordVPN split tunneling is set to **exclude from VPN** only the three Tailscale executables, the Opticon UI and CLI (`Opticon.exe` and `Admin\Cli\opticon.exe`), the exact Windows OpenSSH client at `%WINDIR%\System32\OpenSSH\ssh.exe`, and the pinned `rustdesk.exe`. This lets private mesh traffic coexist with NordVPN; RustDesk remains firewall-restricted to Tailscale IPv4, and the Opticon system checks detect drift in this exact set and the NordLynx default route.


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
- serving the small public release manifest (the signed reusable role bundles themselves are private S3 objects delivered through CloudFront);
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

Provision the AWS transport once from an authenticated operator AWS CLI (the
script refuses every account except `053663732727`):

```powershell
Set-Location 'C:\source\egos-nice-windows\opticon'
.\infrastructure\aws\Provision-OpticonReleaseDistribution.ps1
```

The normal `build.ps1` command checks the live target-release manifest after a
successful package build. If the source version is not fully deployed, it runs
`fly-headscale\scripts\Publish-OpticonBundles.ps1` automatically. Automatic
publication is allowed only from a clean `main` exactly synchronized with
`origin/main`; missing credentials or an incomplete deployment fail the build
loudly. The publisher prints the selected version, object sizes, SHA-256 values,
and distribution hostname.


The Docker build intentionally excludes large Opticon ZIPs and bundle staging outputs. `Publish-OpticonBundles.ps1` discovers the next immutable S3 version, builds and signs both bundles, uploads with parallel multipart transfer and S3 SHA-256 checksums, verifies CloudFront HEAD, Range, and a full streamed SHA-256, then atomically deploys the Fly manifest containing only those CloudFront URLs. The gateway accepts only HTTPS CloudFront URLs that exactly match the manifest version and safe filename. Legacy Fly-volume bundles are retained but no new bundle is sent through its HMAC chunk endpoint.

Useful diagnostics:

```powershell
flyctl logs --app taildesk-egokick-control
flyctl status --app taildesk-egokick-control --all
flyctl releases --app taildesk-egokick-control
```

Never copy the Fly token into this repository, `fly.toml`, the image, Opticon configuration, or an invitation. If the dedicated IPv4 changes, update `fly-headscale\config.yaml`, rebuild the signed `Taildesk.RouteKeeper.exe`, and replace the installed route task through the signed installer before considering the migration complete.

Fly CLI references: [deploy](https://fly.io/docs/flyctl/deploy/), [status](https://fly.io/docs/flyctl/status/), [IP management](https://fly.io/docs/flyctl/ips/).

## Build and install

Production packaging requires exact .NET SDK 10.0.302, a publicly trusted product
code-signing certificate, a separate offline source-release certificate, and an
RFC 3161 timestamp service:

```powershell
Set-Location 'C:\source\egos-nice-windows\opticon'
.\build.ps1 -Runtime win-x64 -BuildProfile Production `
  -CodeSigningCertificateThumbprint '<product-code-signing-thumbprint>' `
  -SourceReleaseSigningCertificateThumbprint '<offline-release-thumbprint>'
```

The production build starts from a clean committed tree, creates fresh isolated outputs, signs every executable with the product certificate and RFC 3161 timestamp, then signs the exact package manifest with the offline source-release key. It writes `dist\Opticon-CommandCenter-win-x64.zip` and checks the same version's hosted target release. Extract the ZIP, verify the Windows publisher, and open only `Install-Opticon.exe`; no loose PowerShell entry point is distributed.

For an owner-controlled fleet without a public publisher identity, use the explicit
`OwnerManaged` profile with separate self-signed product and source-release keys.
Windows shows **Unknown Publisher** at the initial UAC boundary, but the invitation
page and bootstrap still enforce exact bootstrap/source hashes, signer pins, the
RSA-PSS source manifest, and an RFC 3161 timestamp. This profile must not be
represented as publicly trusted software.

Hosted invitations no longer carry prebuilt Agent binaries. The invitation pins the exact bootstrap and source archive by version, filename, size, SHA-256, signing profile, source-release key, product signer, stable .NET 10 SDK policy, output runtime pack, and target runtime. The recipient verifies and builds that source locally with any stable .NET 10 SDK (`10.*.*`), after an explicit install prompt if none is present, and Setup then enrolls the device into the private mesh using the encrypted one-time invitation.

Developer packages require explicit, separate development certificates plus `-BuildProfile Developer -SkipTargetReleaseDeployment`; their filename contains `DEV-UNTRUSTED` and they cannot be published. A skipped production build does not make that version available through **Update Opticon**.

For deeper implementation details, continue with `docs\ARCHITECTURE.md` and `docs\SECURITY.md`, then read the code under `src\Taildesk.Admin`, `src\Taildesk.Agent`, `src\Taildesk.Setup`, and `src\Taildesk.Shared`.
