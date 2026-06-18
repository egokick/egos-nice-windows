# WorkVM status

Last updated: 2026-06-05

## Done

- Created the working folder at `C:\source\egos-nice-windows\workvm`.
- Installed VirtualBox 7.2.8.
- Downloaded the official Microsoft Windows 11 ISO to `C:\source\egos-nice-windows\workvm\downloads\Win11_25H2_English_x64_v2.iso`.
- Created the Windows VM named `WorkRDP`.
- Installed Windows, 1Password, and KeePass in the guest.
- Installed the matching Oracle VirtualBox Extension Pack 7.2.8 on the host.
- Configured Edge and Chrome policy in the guest to force-install the 1Password browser extension.
- Created the guest desktop keepalive launcher: `Start WorkVM Keepalive.cmd`.
- Fixed the StayActive WorkVM launcher so it removes stale installer media, disables the firmware boot menu, and boots `WorkRDP` from the Windows OS disk.
- Updated the StayActive WorkVM menu to expose explicit `Switch Bluetooth to VM` and `Return Bluetooth to laptop` actions. `Switch Bluetooth to VM` now runs `scripts\37-repair-bluetooth-passthrough.ps1`.
- Added `scripts\35-enable-copy-paste.ps1` and updated `scripts\34-start-workvm-ready.ps1` so VirtualBox always enables bidirectional clipboard, file transfer, and drag/drop.

## Copy/paste status

VirtualBox host settings are enabled:

- Clipboard: bidirectional.
- Clipboard file transfers: on.
- Drag/drop: bidirectional.

Live VM check currently shows `GuestAdditionsRunLevel=0`. That means the host settings are correct, but copy/paste will not actually work until Oracle VirtualBox Guest Additions is repaired inside the guest. A repair attempt was launched from the mounted Guest Additions ISO, Windows showed a UAC prompt for `Oracle VirtualBox Guest Additions` from verified publisher `Oracle America, Inc.`, and the guest service still did not register afterward.

## Display status

The VM is configured to prefer `1920x1080x32`:

- `CustomVideoMode1=1920x1080x32`
- `GUI/LastGuestSizeHint=1920,1080`
- `VBoxInternal2/EfiGraphicsResolution=1920x1080`

The start scripts now reapply that preference every time the VM starts. The live VM may remain at `1024x768` until VirtualBox Guest Additions is repaired, because Windows in the guest is not currently accepting runtime display resize hints.

## Bluetooth status

The laptop Bluetooth adapter is a MediaTek USB radio, `13d3:3602`.

VirtualBox can see it, and the VM has a USB filter for it. A phone passkey QR flow still needs Bluetooth proximity from the browser's machine. Because the browser runs inside the VM, the guest must own a Bluetooth radio during the passkey prompt.

With only the built-in laptop Bluetooth adapter, VirtualBox cannot share that adapter between host Windows and guest Windows at the same time. For passkeys in the VM, the host must release the MediaTek adapter while the VM uses it. Host Bluetooth will be unavailable during that handoff. A dedicated USB Bluetooth dongle would avoid that tradeoff, but this setup uses the built-in adapter because no dongle is available.

Current tested result:

- `scripts\37-repair-bluetooth-passthrough.ps1` can reset the host MediaTek device and attach it to `WorkRDP`.
- `VBoxManage showvminfo WorkRDP` has shown the attached device as `VendorId=0x13d3`, `ProductId=0x3602`, `Product=Wireless_Device`.
- Host `VBoxManage list usbhost` currently reports the MediaTek `13d3:3602` device state as `Captured`.
- Inside the VM, Windows Settings shows `Wireless_Device` under `Other devices` with `Driver error`.
- Inside the VM, `pnputil /enum-devices /problem` reported `Generic Bluetooth Adapter`, driver `bth.inf`, problem code `43`.
- Therefore Bluetooth is not verified working inside the VM yet.

The matching host MediaTek Bluetooth driver package was copied into `drivers\mediatek-bluetooth` and mounted into the VM as `.cache\mediatek-bluetooth-driver-v2.iso`. The package INF does match `USB\VID_13D3&PID_3602&MI_00`. An elevated guest install/rebind command was launched twice, but the VM UAC prompt was canceled both times, so the driver has not been installed/bound in the guest yet.

Run this command from a normal PowerShell to repair/retry the VirtualBox passthrough attachment. It will prompt for administrator approval:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\source\egos-nice-windows\workvm\scripts\37-repair-bluetooth-passthrough.ps1"
```

That script resets the host MediaTek Bluetooth PnP device, rebuilds the VM USB filter, starts `WorkRDP`, and attaches the adapter to the VM. It includes a timeout for stuck VirtualBox `poweroff` calls.

To give Bluetooth back to the host later:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "C:\source\egos-nice-windows\workvm\scripts\33-return-laptop-bluetooth-to-host.ps1"
```
