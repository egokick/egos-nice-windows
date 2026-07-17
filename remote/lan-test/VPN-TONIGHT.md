# VPN-only two-laptop setup

This is the shortest supported path to use the second laptop as a self-hosted
Headscale exit node. It deliberately skips MeshCentral screen/files and
inventory mapping. Do not paste an enrollment command or key into chat, email,
or source control.

This first proof requires both laptops to be on the same trusted private LAN.
It does **not** make the controller reachable from an airport or hotel. Keep
the controller laptop online on the LAN for this test; a public, always-on
deployment is a separate follow-up before relying on it while travelling.

## 1. Finish the controller laptop

On this laptop, complete the LAN bootstrap and finalization described in
[`README.md`](README.md). The finalization command installs the protected
Windows enrollment controller and opens only the private-LAN HTTPS/STUN ports.

Once finalization succeeds, run this as Administrator to trust the local CA and
record the printed 64-character certificate SHA-256 fingerprint:

```powershell
.\scripts\Install-CaddyRoot.ps1
```

Transfer only the public file `certs\caddy-root.crt` to the same path in the
second laptop's checkout through a trusted local method (USB or an authenticated
LAN share). Verify its SHA-256 fingerprint directly between the two laptops.

## 2. Prepare the second laptop as the exit node

On the second laptop, pull the same branch/commit, place the public certificate
at `remote\lan-test\certs\caddy-root.crt`, and open Administrator PowerShell
at the repository root. Then run:

```powershell
.\remote\lan-test\scripts\Join-LanTestDevice.ps1 `
  -ServerIp <controller-lan-ip> `
  -ExpectedCertificateSha256 <verified-64-hex-fingerprint> `
  -AdvertiseExitNode `
  -InstallTailscale
```

The script changes only the StayActive hosts block, trusts the verified public
root certificate, optionally installs the open-source Tailscale client, and
prompts for (without echoing) the single-use command issued by:

```text
StayActive tray > Remotes > Add device > Exit-capable
```

Paste that command only into the prompt. It is valid for 15 minutes and only
once. On success, the second laptop advertises its default route but cannot be
used as an exit node until the controller approves it.

## 3. Approve the exit node and join this laptop

Back on the controller laptop, identify the newly enrolled Headscale node and
approve only its advertised default route:

```powershell
.\remote\lan-test\scripts\Approve-LanTestExitNode.ps1 -NodeId <headscale-node-id> -ApproveExitNode
```

Join this laptop to the same Headscale network using the same
`Join-LanTestDevice.ps1` script without `-AdvertiseExitNode`, after issuing a
separate standard-device command from StayActive. Do not reuse the command used
by the second laptop.

Use the StayActive **Remotes** exit-node action once the peer appears. If a
minimal command-line fallback is needed, obtain the second laptop's Tailscale
IPv4 address from `tailscale status --json`, then run on this laptop:

```powershell
.\remote\lan-test\scripts\Set-LanTestExitRoute.ps1 -ExitNodeIp <second-laptop-tailscale-ip>
```

To restore direct routing:

```powershell
.\remote\lan-test\scripts\Set-LanTestExitRoute.ps1 -Clear
```

## 4. Verify

First verify the two peers appear in `tailscale status`. For a real changed
public-egress test, put the exit laptop on a different WAN/hotspot and query an
endpoint you control that reports the caller address. Two laptops sharing one
Wi-Fi NAT can prove peer connectivity and route selection, but their public IP
will naturally look the same.
