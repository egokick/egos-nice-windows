# StayActive VPN control plane on Fly.io

This directory deploys the urgent VPN-only control plane. It runs the
digest-pinned Headscale and Caddy binaries in one always-on Fly Machine, stores
Headscale state on one encrypted volume, accepts raw private TLS on TCP 443,
and exposes the embedded private DERP STUN listener on UDP 3478.

The public Headscale REST API is blocked by Caddy. Initial administration uses
Fly Machine Exec and Headscale's local CLI, so no long-lived Headscale API key
is stored on Fly or exposed publicly.

`headscale.stayactive.test` is intentionally retained as the client login URL.
Because `.test` is a reserved name, every client pins the dedicated Fly IPv4
in its hosts file and verifies the committed public Caddy root certificate by
SHA-256 before trusting it. The matching CA private key remains only on the
encrypted Fly volume and must never be exported.

The deployed application is `stayactive-headscale-egokick` in `lhr`, with the
encrypted `stayactive_headscale_data` volume and dedicated IPv4 `137.66.29.4`.

## Security boundary

- Clients use only `https://headscale.stayactive.test`; the Windows machine
  policy pins that URL so the GUI cannot switch the device to Tailscale's
  hosted coordination service.
- The Headscale REST API, metrics, and debug paths are blocked at the public
  edge. Headscale management listeners remain on loopback inside the Machine.
- The DERP map contains only the embedded StayActive DERP. Tailscale support
  log transmission and posture reporting are disabled on each Windows client.
- No Headscale API key or Fly secret exists in the deployed Machine. The
  owner-side scripts use the controller laptop's existing Fly deployment token
  only through non-interactive Machine Exec; they must never be called by the
  StayActive tray process or copied to another device.
- The official Windows client still performs its built-in software-update
  check even when the update preference is disabled. That is not a hosted
  coordination account or control plane; applying updates remains disabled.

The scripts in `scripts` are the urgent owner-operated VPN path. They are not a
replacement for the production enrollment-only Windows broker described in
`remote/README.md` and must not be wired into the tray.

## Enroll the other laptop as an exit node

Do not create its ticket until the other person is at the laptop. On this
controller laptop, run:

```powershell
.\remote\fly-headscale\scripts\New-FlyEnrollment.ps1 -ExitCapable
```

The script shows one exact join command and a revocable ticket ID. The command
is valid once for 15 minutes. To cancel it before use:

```powershell
.\remote\fly-headscale\scripts\Revoke-FlyEnrollment.ps1 -EnrollmentId <ticket-id>
```

On the other laptop, from its existing repository checkout, run these two
commands. The second command opens one UAC prompt and then one hidden paste
prompt; paste the exact one-time join command there.

```powershell
git pull --ff-only origin main
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\remote\fly-headscale\client\Join-FlyExitNode.ps1
```

The bootstrap pins the public IP and certificate, installs Tailscale if needed,
pins the Windows client to the self-hosted URL and exit role, joins unattended,
advertises both default routes, disables support-log transmission, and disables
AC sleep/hibernate. Leave the laptop plugged in, online, awake, and with its lid
open. The final line records its current public IPv4 for comparison.

## Approve and use the exit node

Back on this controller laptop:

```powershell
.\remote\fly-headscale\scripts\Get-FlyNodes.ps1
.\remote\fly-headscale\scripts\Approve-FlyExitNode.ps1 -NodeId <other-node-id> -ApproveExitNode
.\remote\fly-headscale\scripts\Use-FlyExitNode.ps1 -NodeName <other-computer-name>
```

Approval is refused unless the selected node has the exit-capable tag and is
advertising both IPv4 and IPv6 default routes. Enabling is refused unless that
same peer is online and Headscale has approved it. The final command disables
local-LAN bypass, enables exit-node DNS, pings the peer, resolves a hostname,
and prints the public IPv4 observed through the other laptop.

Disable exit routing at any time with:

```powershell
.\remote\fly-headscale\scripts\Clear-FlyExitNode.ps1
```

The remote laptop is not ready for travel use until the observed IPv4 while
enabled matches the IPv4 printed on the remote laptop, and disabling the exit
node restores this laptop's own public address. If IPv6 is available, verify it
also exits remotely or fails closed rather than retaining a local IPv6 route.
