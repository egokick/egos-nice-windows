# StayActive Docker work browser

This folder contains the Docker/WSL replacement for the WorkRDP VirtualBox
guest. Chrome, WebAuthn, BlueZ, and the graphical work desktop run inside the
container. The Windows browser is only a loopback noVNC pixel/input viewer.

The implementation is intentionally transactional: Bluetooth hardware
`13d3:3602` is never removed from Windows PnP. It is released from VirtualBox,
verified on Windows, attached to the dedicated `StayActiveDocker` WSL
distribution, and returned through the reverse sequence.

Primary commands
----------------

Run these from PowerShell. They request UAC when needed.

```powershell
.\docker-work\scripts\setup.ps1
.\docker-work\scripts\open-work.ps1
.\docker-work\scripts\put-bluetooth-on-laptop.ps1
.\docker-work\scripts\put-bluetooth-on-container.ps1
.\docker-work\scripts\verify.ps1
.\docker-work\scripts\open-passkey-test.ps1
```

`verify.ps1` performs ten complete ownership cycles by default, plus forced
container-crash and WSL-termination recovery tests. It leaves Bluetooth on the
work browser unless `-LeaveBluetoothOnLaptop` is supplied.

Logs and generated state are under ignored `.cache` and `.state` directories.
The pinned custom kernel artifacts are stored under
`%LOCALAPPDATA%\StayActive\wsl-kernel`. The original `.wslconfig` is backed up
before any kernel keys are merged. `rollback-wsl-kernel.ps1` restores it.

The last acceptance step cannot be automated: run `open-passkey-test.ps1`,
choose the phone/tablet option inside container Chrome, scan the QR code, and
approve on the managed phone. The page reports PASS only when Chrome says the
returned credential used the hybrid transport.

