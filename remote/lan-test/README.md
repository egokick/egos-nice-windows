# StayActive Remotes: private LAN validation stack

This folder is a controlled two-laptop validation environment for StayActive
Remotes. It is deliberately separate from the public deployment template in
the parent folder.

For a time-sensitive VPN/exit-node-only setup, follow
[VPN-TONIGHT.md](VPN-TONIGHT.md). It intentionally defers the screen, file,
and inventory portions of this stack.

It provides:

- Headscale plus self-hosted DERP/STUN for the private mesh
- MeshCentral for consented view-only screen sessions and Files sessions
- Keycloak for locally controlled OIDC users, scopes, and administrator role
- RemoteHub for signed inventory and audit data
- a Windows enrollment controller for narrowly authorized, one-time Headscale
  enrollment tickets
- Caddy internal-CA HTTPS, exposed only after one-time bootstrap is complete

No application port, database, Keycloak port, MeshCentral port, or Headscale
API port is published directly. The only eventual inbound firewall exceptions
are local-subnet TCP 443 and UDP 3478. Caddy's controller-only Headscale API
listener is permanently bound to host loopback on TCP 4443, and the Windows
controller's TCP 5091 firewall rule permits only Caddy's fixed control-network
peer. During bootstrap, HTTPS is bound to 127.0.0.1 only.

## Preconditions

- Docker Desktop is running on this laptop.
- Both laptops are on the same trusted private LAN for the first test.
- PowerShell commands that change hosts, firewall, or LocalMachine trust run
  from an Administrator PowerShell window.
- The eventual remote client uses a supported Tailscale client version for
  Headscale 0.29 or newer. It must point at this Headscale server, not a
  Tailscale-hosted control plane.

Do not copy files from generated, state, or secrets. The only certificate file
to transfer to the other laptop is the public certs\caddy-root.crt file.

## Bootstrap this laptop

Run initialization from a normal PowerShell window. Replace the address with
this laptop's RFC1918 Wi-Fi address.

~~~powershell
.\scripts\Initialize-LanTest.ps1 -LanIp 192.168.1.168
~~~

The initializer refuses a dirty Git tree, resolves images to immutable
digests, generates local secrets, writes a deny-all Headscale bootstrap policy,
and binds Caddy to loopback.

In an Administrator PowerShell window, install the temporary local name mapping:

~~~powershell
.\scripts\Set-LanTestHosts.ps1 -Mode Bootstrap
~~~

Start the bootstrap services:

~~~powershell
.\scripts\Start-LanTest.ps1
~~~

Then trust the generated Caddy root on this laptop:

~~~powershell
.\scripts\Install-CaddyRoot.ps1
~~~

Open https://meshcentral.stayactive.test in a local browser and create the
single initial MeshCentral administrator. Do this while the stack is still
loopback-only. Immediately create a normal MeshCentral operator account with
view-only desktop and Files permissions; do not use the server administrator
for ordinary remote actions.

Only after that administrator exists, publish the two reviewed LAN ports:

~~~powershell
.\scripts\Finalize-LanTest.ps1 -MeshCentralAdministratorCreated -EnableLan
~~~

This creates the Headscale policy owner, applies the reviewed tagged policy,
force-reloads Caddy while it is still loopback-only, and verifies that a
LAN/public request to Headscale `/api/v1` receives `404`. It then adds the two
local-subnet firewall rules and rebinds Caddy from loopback to the configured
private IP.

### Install the Windows enrollment controller

`Finalize-LanTest.ps1` runs the reviewed Windows controller installation after
it creates the Headscale policy owner. It discovers the WSL virtual-interface
address, writes that private address to `WINDOWS_ENROLLMENT_CONTROLLER_IP`,
recreates Caddy with the private ticket route, installs the controller as the
`StayActiveEnrollmentController` service under the dedicated
non-administrator `StayActiveHeadscaleController` identity, creates the
controller journal and published service files under the ACL-protected
`%ProgramData%\StayActiveRemotes\EnrollmentController` root, and verifies the
controller firewall rule and Caddy-to-controller route before publishing the
LAN endpoint. That root is intentionally outside the repository's
`remote/lan-test/state` tree so the interactive tray user cannot replace service
binaries, configuration, or journal files.

The raw controller key is captured only in memory and passed once on standard
input to the provisioning mode. It is stored only in that service account's
Windows Credential Manager target:

```text
StayActive/HeadscaleController/v1
```

Do not create, copy, mount, or rotate a Docker-side controller-key file. The
controller service must be the only process that can read the Credential Manager
entry. Caddy must be configured with `WINDOWS_ENROLLMENT_CONTROLLER_IP` for the
private Windows endpoint, while the controller firewall permits TCP 5091 only
from Caddy's reviewed control-network address. The installer verifies its
private health endpoint before the tray can issue tickets.

`headscale-controller.stayactive.test` is a controller-only name mapped to
`127.0.0.1` on this laptop. Caddy serves it only on TCP 4443 and only for the
controller's Headscale `/api/v1/*` calls. Never publish that name or port on the
LAN, and never point another laptop at it.

## Prepare the second laptop

Copy only the public certs\caddy-root.crt file through a trusted out-of-band
method. On the server laptop, run Install-CaddyRoot.ps1 once to display the
certificate SHA-256 fingerprint, then verify that fingerprint separately on
the second laptop before trusting the copied file. In an Administrator
PowerShell window on the second laptop:

~~~powershell
.\scripts\Set-LanTestHosts.ps1 -Mode Lan -ServerIp 192.168.1.168
.\scripts\Install-CaddyRoot.ps1 -CertificatePath C:\safe-path\caddy-root.crt -ExpectedCertificateSha256 <verified-64-hex-fingerprint>
~~~

Install the supported open-source Tailscale client and the current StayActive
build on both laptops. On the server laptop, use the tray menu:

~~~text
StayActive tray > Remotes > Add device
~~~

The flow obtains a fresh interactive OIDC authorization for the dedicated
`stayactive-remotes-enrollment` public client, then requests one **15-minute**,
one-time ticket from the Windows enrollment controller. It does not reuse the
normal tray token or expose the controller's Headscale API credential. Transfer
the displayed ticket only through a trusted, owner-controlled channel and redeem
it immediately on the second laptop. The client enrolls against:

~~~text
https://headscale.stayactive.test
~~~

`New-LanTestEnrollmentKey.ps1` is legacy break-glass tooling and is not part of
the accepted enrollment path. Do not use it for normal device enrollment or
paste any output into chat, email, issue trackers, or source control.

The other laptop needs tag:stayactive and tag:stayactive-exit. Once it is
connected, advertise its default route with the Tailscale client. Find its
numeric Headscale node ID, then explicitly approve only its advertised default
route before selecting it in the StayActive tray menu:

~~~powershell
.\scripts\Approve-LanTestExitNode.ps1 -NodeId 123 -ApproveExitNode
~~~

## Inventory and RemoteHub

Open https://remotehub.stayactive.test/admin/ and sign in as the locally
generated stayactive-operator user. Its temporary password is in the protected
local file secrets\operator-bootstrap.json; retrieve it only on this laptop
and change it at first sign-in.

For every endpoint, add a RemoteHub inventory mapping with:

- the exact Headscale/Tailscale peer ID from the client status JSON
- the bare MeshCentral node ID, not the node// record prefix
- explicit owner and coarse-location opt-in choices

Install the MeshCentral agent from the MeshCentral device group on both
laptops. Screen and file actions in StayActive open that mapped device's
MeshCentral session. A remote owner must accept the desktop consent prompt;
file transfer requires consent to the Files session. This stack intentionally
does not grant terminal access through the standard Remotes operator role.

## Acceptance checks

1. The StayActive tray's **Add device** flow requires a fresh authorization,
   issues exactly one one-use ticket with a 15-minute lifetime, and records its
   issuance/revocation without persisting the raw ticket key.
2. The StayActive tray's Remotes submenu lists both tagged peers with their
   opted-in labels and coarse locations.
3. Selecting an exit node turns routing on; deselecting it turns routing off.
4. A desktop request shows a target-side consent prompt and opens a view-only
   MeshCentral session after acceptance.
5. Upload, download, and request-file actions occur only after a Files session
   is accepted by the target user.
6. RemoteHub records inventory changes and action audit entries with the
   signed journal intact.
7. Confirm changed public egress only when the remote exit laptop is on a
   different WAN or hotspot. Two laptops sharing one Wi-Fi WAN cannot prove a
   changed public IP, although they can validate mesh routing and the toggle.

Bluetooth adapter virtualization and raw passkey relay are intentionally not
part of this stack. Relaying raw HCI or pairing material would expose
authentication secrets. Use the local device's platform authenticator or
WebAuthn instead.

## References

- https://headscale.net/stable/setup/requirements/
- https://headscale.net/stable/ref/policy/
- https://headscale.net/stable/ref/routes/
- https://www.keycloak.org/server/reverseproxy
- https://docs.meshcentral.com/meshcentral/config/