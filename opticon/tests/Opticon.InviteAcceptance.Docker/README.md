# Opticon invitation acceptance container

Run from the repository root on the Opticon command center:

```powershell
.\tests\Test-OpticonInviteDocker.ps1
```

The test creates a disposable managed-device invitation, accepts it inside an
isolated read-only Linux container, and validates the live landing page,
fragment-key encryption, invitation signature and fields, role bundle,
SHA-256/size pins, safe ZIP layout, and the pinned x64 Tailscale and RustDesk
downloads. Windows then verifies Authenticode on the exact downloaded files.
The Fly invitation, Headscale key, local record, temporary files, and container
are removed even when the test fails. Docker Desktop is returned to its prior
stopped state when the script had to start it.

Linux Docker cannot execute Windows UAC, `msiexec`, Windows services, scheduled
tasks, or firewall changes. Those OS-specific mutations still require a Windows
VM or physical test PC. This harness covers the full invitation delivery and
security-validation path that caused the earlier target-machine failures.
