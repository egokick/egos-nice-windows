# WorkVM setup

Current machine state is tracked in `C:\source\egos-nice-windows\workvm\STATUS.md`. Read that first; it reflects the VM that is already built on this laptop.

This folder is for a local VM that keeps a browser-based RDP session active while the host laptop remains free for other work.

The important design choice is Bluetooth. Phone-based passkeys and some password-manager flows use WebAuthn/CTAP over Bluetooth proximity. The VM therefore needs its own Bluetooth radio path. This setup supports two paths:

- Preferred: a dedicated USB Bluetooth dongle passed into the guest.
- No-dongle path for this laptop: pass the built-in MediaTek USB Bluetooth radio into the VM. Host Windows Bluetooth will stop working while the VM owns that radio.

## Host findings

- Host OS: Windows 11 Home.
- RAM/CPU: enough for a Windows guest; the default VM profile uses 8 GB RAM and 4 vCPUs.
- Existing host tools: `winget` and Chocolatey are available.
- VirtualBox/VMware/QEMU: not currently on PATH.
- Current shell is not elevated, so VirtualBox driver installation must be run from an elevated PowerShell.
- Host already has Chrome and KeePass installed, but the VM must have its own browser/password setup.

## Required hardware and software

- A Windows 11 ISO for the guest installer.
- A Bluetooth radio for the VM. On this laptop, VirtualBox sees the built-in MediaTek adapter as USB `0x13d3:0x3602`, so it can be passed into the VM for QR/passkey proximity flows.
- Admin rights once on the host to install VirtualBox.

## Quick start

Open an elevated PowerShell and run:

```powershell
cd C:\source\egos-nice-windows\workvm
.\scripts\00-host-check.ps1
.\scripts\10-install-virtualbox.ps1
```

Reboot if the VirtualBox installer asks for it. Then create the VM:

```powershell
cd C:\source\egos-nice-windows\workvm
.\scripts\05-find-windows-iso.ps1
.\scripts\20-create-vm.ps1 -IsoPath "C:\real\path\to\Win11.iso"
.\scripts\40-start-vm.ps1
```

If no ISO is found:

```powershell
.\scripts\05-find-windows-iso.ps1 -OpenMicrosoftDownloadPage
```

Download the official Windows 11 x64 ISO from Microsoft, then re-run `20-create-vm.ps1` with the downloaded file path.

Install Windows in the VM. After Windows is installed, add the laptop Bluetooth adapter filter:

```powershell
.\scripts\31-use-laptop-bluetooth.ps1
```

This adds a VirtualBox USB filter for `0x13d3:0x3602`, the MediaTek Bluetooth adapter seen on this host. Power off and restart the VM so it can capture the adapter.

For a dedicated Bluetooth dongle instead, list attached USB devices:

```powershell
.\scripts\30-add-usb-bluetooth-filter.ps1
```

Then run `30-add-usb-bluetooth-filter.ps1` with the dongle's `VendorId` and `ProductId`.

## Guest setup

Inside the Windows guest, install VirtualBox Guest Additions first so the shared folder can automount. The shared folder is configured as `workvm` and should appear as a drive such as `W:`.

Then run this inside the guest:

```powershell
W:\guest\install-guest-browser-tools.ps1
```

By default this installs:

- Google Chrome
- 1Password
- AutoHotkey v2

For KeePass too:

```powershell
W:\guest\install-guest-browser-tools.ps1 -InstallKeePass
```

## Bluetooth/passkey verification

Before using the RDP site, verify the auth chain:

1. In the guest, confirm Device Manager shows a Bluetooth adapter.
2. Keep Bluetooth enabled on the phone.
3. Open Chrome in the guest and test a passkey or password-manager unlock flow.
4. If the website shows a QR code, scan it from the phone and let the phone complete proximity verification.
5. If the prompt never reaches the phone, the guest probably does not own the Bluetooth radio.

Do this before debugging the RDP keepalive. If passkeys do not work, the keepalive will not matter.

## RDP keepalive workflow

1. In the guest, open the browser-based RDP session.
2. Keep that browser tab focused inside the VM.
3. Start `WorkVM Keep RDP Alive.ahk` from the guest desktop.
4. Continue working on the host laptop.

The AutoHotkey script moves the guest mouse by one pixel and returns it on a timer. This sends input through the focused browser/RDP surface inside the VM, while host focus remains elsewhere.

Hotkeys in the guest:

- `Ctrl+Alt+P`: pause/resume keepalive.
- `Ctrl+Alt+Q`: quit keepalive.

Check your organization's policy before using any keepalive automation.

## Why not Hyper-V or a Chrome extension?

Hyper-V on Windows does not provide simple native USB passthrough for the Bluetooth/authenticator path. Chrome extensions can keep a browser or display awake, but they cannot reliably generate trusted input into a hidden browser RDP canvas. The VM approach gives the RDP browser its own focused desktop and gives phone auth a direct Bluetooth path.
