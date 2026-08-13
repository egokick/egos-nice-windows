# Opticon local Docker end-to-end test

## Mandatory installer-change gate

Run this test whenever a change can affect any part of the installation or
enrollment path. This includes changes to:

- `Taildesk.Setup`, the hosted/source bootstrap, preflight, elevation plan, or
  installer transaction and recovery;
- invitation schemas, signing, encryption, expiry, one-use credentials, landing
  pages, download routes, or acceptance;
- source-release construction, allowlists, manifests, certificates, signing
  policy, SDK/runtime policy, dependency pins, or artifact validation;
- the Redeploy/release preflight path, gateway release endpoints, Headscale
  configuration/policy, or local equivalents of Fly/S3/CloudFront transport;
- Tailscale installation or enrollment arguments, hostname normalization,
  tags, users, routes, or authoritative node checks;
- the production enrollment transaction, persisted device records, invitation
  consumption, or the Command Center device projection/connection state;
- tests or refactors that move any of those responsibilities, even if the
  intended behavior is unchanged.

The change is not complete merely because unit tests pass. The full E2E is the
required integration gate because installer failures commonly occur at the
boundaries between independently valid components.

## Final required run

Prerequisites:

- Windows with PowerShell, Docker Desktop, the stable `.NET 10.*.*` SDK, and the
  Windows SDK x64 `signtool.exe`;
- the product and source-release certificates referenced by the current staged
  manifest available in the current-user certificate store;
- network access for package restore, the RFC 3161 timestamp service, and any
  required container images;
- all intended installer changes saved in the checkout;
- a new immutable release version when the current version has already been
  built with different bytes. Keep `Directory.Build.props` and the default
  versions in `Build-OpticonBundles.ps1`, `Build-OpticonSourceRelease.ps1`, and
  `Publish-OpticonSourceRelease.ps1` synchronized.

From an elevated PowerShell in the `opticon` directory, run:

```powershell
.\tests\Test-OpticonLocalDockerE2E.ps1
```

Do not use `-SkipReleaseBuild` for the final pre-merge or pre-release result. The
default command must build and sign the exact changed checkout. Do not use
`-KeepEnvironment` for the final result either; cleanup is part of the test.

The run is successful only if it exits with code `0` and prints both:

```text
PASS Command Center displays Opticon Docker E2E device ... as connected (Tailscale only).
PASS Opticon local Docker E2E: built/deployed source, accepted invite, connected device, and Command Center visibility verified.
```

Treat any earlier exception, nonzero exit, missing final PASS line, retained
test container, or retained temporary CA as a failure. Fix the failure and rerun
the complete command; do not substitute a partial manual check.

## Theory of the test

The test follows a simple rule: execute production code and production formats
through the entire lifecycle, and replace only infrastructure or operating-system
edges that cannot safely run in a Docker-only local test. It does not upload to
the real S3 bucket, redeploy the real Fly gateway, mutate an external tailnet, or
pretend that Linux can execute Windows UAC and services.

| Production responsibility | What the E2E executes |
| --- | --- |
| Build and sign the release selected by Redeploy | The real source-release builder, allowlist, product signing, source-manifest signing, staging, and verification for the checkout version |
| Deploy and serve the release | The real gateway image and release manifest, with Docker Compose and a loopback Caddy origin replacing Fly plus S3/CloudFront |
| Preflight and create an invite | Production `ReleaseDeploymentService.PrepareAsync(forceRedeploy: true)`, `InviteBundleService`, signing/encryption, HMAC-protected gateway endpoints, and a real one-use Headscale key |
| Accept the invite on a device | A locked-down Linux adapter independently verifies the public landing/download contract, invitation encryption/signature/schema, exact artifact metadata and SHA-256, tamper rejection, and safe archive shape |
| Join the managed network | Real `tailscaled`, the official digest-pinned Linux client, and a disposable real Headscale control plane using the production role policy |
| Commit enrollment | Production `EnrollmentService`, including authoritative Headscale identity checks, invite-secret validation, one-use consumption, and durable isolated Admin state |
| Show the result to the operator | Production `MainViewModel.RefreshAsync`; the test passes only when the device appears in the Command Center collection as `TailscaleOnly` |

This structure gives meaning to “it worked in E2E”: the release producer,
gateway, invitation consumer, network control plane, enrollment transaction, and
Command Center projection agree on the same real version, hashes, identities,
and one-use state. A mock-only test could pass while those contracts disagree.

The device-side adapter is intentionally an independent consumer at the
cross-platform boundary. Values that must stay aligned with production, such as
the invitation schema and normalized Tailscale hostname, are supplied by the
compiled production .NET code rather than duplicated as stale test constants.

## What a pass proves

A full pass proves that the exact checkout can produce a newly signed source
release; the local real gateway can serve it; a real signed/encrypted invitation
can be created and accepted; the pinned archive survives exact size/hash and
negative tamper checks; a device can join Headscale with the one-use key; the
production enrollment transaction accepts and consumes that identity; and the
production Command Center refresh displays the connected device.

It does not prove Windows-only installer mechanics. If a change touches
UAC/elevation, MSI or dependency installation, Windows services, scheduled
tasks, firewall rules, RustDesk, filesystem ACLs, reboot/crash recovery on
Windows, or the WPF Setup UI, the Docker E2E is still required but must be
followed by an attended Windows installation smoke test on a disposable or
designated test machine.

## Deliberate local divergences

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

## Iteration and diagnosis

The temporary CA, containers, volumes, credentials, and isolated Command Center
state are removed in `finally`.

During local iteration only, `-SkipReleaseBuild` may be used when the manifest
already contains an artifact for the exact checkout version:

```powershell
.\tests\Test-OpticonLocalDockerE2E.ps1 -SkipReleaseBuild
```

That faster run is diagnostic evidence, not the final gate, because it may test
an archive produced before the latest source edit. `-KeepEnvironment` is only
for investigating a failure and intentionally retains disposable containers,
credentials, state, and trust material until manually removed. Never use either
switch as the recorded final result.
