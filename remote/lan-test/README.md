# StayActive Remotes: private LAN validation stack

This folder is a controlled two-laptop validation environment for StayActive
Remotes. It is deliberately separate from the public deployment template in
the parent folder.

It provides:

- Headscale plus self-hosted DERP/STUN for the private mesh
- MeshCentral for consented view-only screen sessions and Files sessions
- Keycloak for locally controlled OIDC users, scopes, and administrator role
- RemoteHub for signed inventory and audit data
- EnrollmentBroker for narrowly authorized, one-time Headscale enrollment tickets
- Caddy internal-CA HTTPS, exposed only after one-time bootstrap is complete

No application port, database, Keycloak port, MeshCentral port, or Headscale
API port is published directly. The only eventual inbound firewall exceptions
are local-subnet TCP 443 and UDP 3478. During bootstrap, HTTPS is bound to
127.0.0.1 only.

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
force-reloads Caddy while it is still loopback-only, and verifies that a host
request to Headscale `/api/v1` receives `404`. Only then does it create the
broker's protected 30-day Headscale API key and journal HMAC key, render its
owner-specific configuration, and prove the isolated non-root service stays
running. The raw API key is never printed, placed in `.env`, or copied into the
RemoteHub service. Finalization migrates an already-initialized bootstrap
installation in place; it does not reinitialize state or overwrite its other
secrets. It then adds the two local-subnet firewall rules and rebinds Caddy
from loopback to the configured private IP.

Rotate the broker-only Headscale API credential explicitly before it expires:

~~~powershell
.\scripts\Rotate-LanTestEnrollmentBrokerApiKey.ps1 -Rotate
~~~

The rotation script first verifies the Headscale policy owner, swaps the
protected file only after the replacement broker survives a restart, then
revokes the prior key. It never displays either raw key.

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
`stayactive-remotes-enrollment` public client, then requests one short-lived,
one-time ticket from EnrollmentBroker. It does not reuse the normal tray token
or expose the broker's Headscale API credential. Transfer the displayed ticket
only through a trusted, owner-controlled channel and redeem it immediately on
the second laptop. The client enrolls against:

~~~text
https://headscale.stayactive.test
~~~

`New-LanTestEnrollmentKey.ps1` remains break-glass recovery tooling only; do
not use it for normal device enrollment or paste its output into chat, email,
issue trackers, or source control.

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
   issues one ticket with a five-to-thirty minute lifetime, and records its
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