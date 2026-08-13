# Opticon local Docker end-to-end test

Run from the `opticon` directory:

```powershell
.\tests\Test-OpticonLocalDockerE2E.ps1
```

The default run invokes the real source-release builder and signer, builds the
real gateway image, runs its production release preflight and HMAC-protected
Headscale/invitation endpoints, accepts and verifies the signed invitation in a
locked-down Linux container, joins a real disposable Headscale tailnet, executes
the production enrollment transaction, and finally runs the real Command Center
refresh path. The test passes only when the container device appears as
`TailscaleOnly` in Command Center.

Only these infrastructure edges differ from production:

- the source archive is mounted read-only instead of uploaded to S3/CloudFront;
- Docker Compose replaces the Fly deployment operation;
- Caddy provides a temporary loopback-only HTTPS origin and current-user CA;
- the Linux device adapter replaces Windows UAC, services, MSI execution,
  firewall changes, RustDesk configuration, and the Windows Agent process.
- the Linux Tailscale CLI uses every production enrollment argument except
  Windows-only `--unattended=true`; Setup and the adapter share the production
  hostname normalizer.
- the device uses the newest published stable Linux container (currently
  Tailscale 1.98.9, digest-pinned) because the Windows-only 1.102.1 MSI version
  has no corresponding official Linux container tag.

The temporary CA, containers, volumes, credentials, and isolated Command Center
state are removed in `finally`. Use `-SkipReleaseBuild` only when the exact
checkout version was already built by a previous run. `-KeepEnvironment` is for
diagnosis and intentionally retains the disposable environment until manually
removed.
